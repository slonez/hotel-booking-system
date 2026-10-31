using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
// Handlers must therefore be idempotent.
public class EventConsumer<TEvent, THandler>(
    string queue,
    string routingKey,
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<EventConsumer<TEvent, THandler>> logger) : BackgroundService
    where THandler : IEventHandler<TEvent>
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private IConnection? _connection;
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = await ConnectAsync(stoppingToken);

        // A durable queue keeps messages while the service is down.
        await channel.ExchangeDeclareAsync(Exchanges.Events, ExchangeType.Topic, durable: true, cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
        await channel.QueueBindAsync(queue, Exchanges.Events, routingKey, cancellationToken: stoppingToken);
        // One message at a time.
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => HandleDeliveryAsync(channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, stoppingToken);
    }

    private async Task HandleDeliveryAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        try
        {
            var message = JsonSerializer.Deserialize<TEvent>(delivery.Body.Span)!;
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<THandler>().HandleAsync(message, ct);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // E.g. the database is down. Wait and put the message back for another try.
            // Phase 6 adds a retry limit and a dead letter queue for messages that never succeed.
            logger.LogError(ex, "Handling message {MessageId} from {Queue} failed; retrying in {Delay}",
                delivery.BasicProperties.MessageId, queue, RetryDelay);
            await Task.Delay(RetryDelay, ct);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, ct);
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Cannot connect to RabbitMQ; retrying in {Delay}", RetryDelay);
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_channel is not null)
            await _channel.DisposeAsync();
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
