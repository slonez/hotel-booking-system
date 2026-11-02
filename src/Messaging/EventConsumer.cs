using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace HotelBooking.Messaging;

public interface IEventHandler<in TEvent>
{
    Task HandleAsync(TEvent message, CancellationToken ct);
}

// Reads events of one type from RabbitMQ and passes them to the handler. A message is acknowledged
// only after it has been handled, so if the service crashes in between, RabbitMQ delivers it again.
// Handlers must therefore be idempotent. Messages that keep failing end up in a dead letter queue (ConsumerQueue).
public class EventConsumer<TEvent, THandler>(
    string queue,
    string routingKey,
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<EventConsumer<TEvent, THandler>> logger) : BackgroundService, IHealthCheck
    where THandler : IEventHandler<TEvent>
{
    private readonly TimeSpan _retryDelay = config.GetValue("Messaging:RetryDelay", TimeSpan.FromSeconds(5));

    private IConnection? _connection;
    private IChannel? _channel;
    private int _stopped;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = await ConnectAsync(stoppingToken);

        // A durable queue keeps messages while the service is down.
        await ConsumerQueue.DeclareAsync(channel, queue, routingKey, stoppingToken);
        // One message at a time.
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => HandleDeliveryAsync(channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, stoppingToken);
    }

    // Unhealthy until connected and during a RabbitMQ outage.
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(_channel is { IsOpen: true }
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"Not connected to RabbitMQ for queue {queue}."));

    private async Task HandleDeliveryAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        var message = Deserialize(delivery);
        if (message is null)
        {
            // Retrying cannot fix a malformed message.
            logger.LogError("Message {MessageId} from {Queue} is malformed; dead lettering it",
                delivery.BasicProperties.MessageId, queue);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
            return;
        }

        if (await TryHandleAsync(message, delivery, ct))
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
        else
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
    }

    // Retries while holding the message, so attempts need no storage. If the service crashes,
    // RabbitMQ delivers the message again and the count starts over.
    private async Task<bool> TryHandleAsync(TEvent message, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<THandler>().HandleAsync(message, ct);
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // E.g. the database is down.
                if (attempt == ConsumerQueue.MaxAttempts)
                {
                    logger.LogError(ex, "Handling message {MessageId} from {Queue} failed {Attempts} times; dead lettering it",
                        delivery.BasicProperties.MessageId, queue, attempt);
                    return false;
                }
                logger.LogWarning(ex, "Handling message {MessageId} from {Queue} failed; retrying in {Delay}",
                    delivery.BasicProperties.MessageId, queue, _retryDelay);
                await Task.Delay(_retryDelay, ct);
            }
        }
    }

    private static TEvent? Deserialize(BasicDeliverEventArgs delivery)
    {
        try
        {
            return JsonSerializer.Deserialize<TEvent>(delivery.Body.Span);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    // RabbitMQ may start after this service, so keep trying. After the first connection
    // the client reconnects and resumes consuming automatically.
    private async Task<IChannel> ConnectAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory { Uri = new Uri(config.GetConnectionString("RabbitMq")!) };
        while (true)
        {
            try
            {
                _connection = await factory.CreateConnectionAsync(ct);
                _channel = await _connection.CreateChannelAsync(cancellationToken: ct);
                return _channel;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Cannot connect to RabbitMQ; retrying in {Delay}", _retryDelay);
                await Task.Delay(_retryDelay, ct);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // The host can call this several times at once, as WebApplicationFactory does. Closing the
        // connection while another call is still closing the channel would throw, so only the first call closes.
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
            return;
        if (_channel is not null)
            await _channel.DisposeAsync();
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
