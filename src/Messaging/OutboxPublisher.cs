using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace HotelBooking.Messaging;

// Publishes saved outbox messages to RabbitMQ. A message is marked published only after
// RabbitMQ confirms it, so a crash in between sends it again (at-least-once delivery).
public class OutboxPublisher<TContext>(
    IServiceScopeFactory scopes,
    IConfiguration config,
    TimeProvider time,
    ILogger<OutboxPublisher<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private const int BatchSize = 100;

    private IConnection? _connection;
    private IChannel? _channel;
    private int _stopped;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PublishPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // RabbitMQ or the database is down. Unpublished messages stay in the table for the next tick.
                logger.LogError(ex, "Publishing outbox messages failed");
            }
        }
    }

    public async Task<int> PublishPendingAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();

        var messages = await db.Set<OutboxMessage>()
            .Where(m => m.PublishedAt == null)
            .OrderBy(m => m.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);
        if (messages.Count == 0)
            return 0;

        var channel = await GetChannelAsync(ct);
        foreach (var message in messages)
        {
            var properties = new BasicProperties
            {
                MessageId = message.Id.ToString(),
                Type = message.Type,
                ContentType = "application/json",
                // Survives a RabbitMQ restart.
                DeliveryMode = DeliveryModes.Persistent
            };
            // Returns when RabbitMQ confirms the message. Throws if it rejects the message or no queue is
            // bound yet (mandatory), so the message stays in the outbox instead of being dropped.
            await channel.BasicPublishAsync(Exchanges.Events, message.Type, mandatory: true,
                properties, Encoding.UTF8.GetBytes(message.Payload), ct);

            // Saved one by one, so a crash re-sends at most one message.
            message.PublishedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return messages.Count;
    }

    // The connection is opened on first use and kept open. The client reconnects automatically if it drops.
    private async Task<IChannel> GetChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true })
            return _channel;

        _connection ??= await new ConnectionFactory { Uri = new Uri(config.GetConnectionString("RabbitMq")!) }
            .CreateConnectionAsync(ct);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        await _channel.ExchangeDeclareAsync(Exchanges.Events, ExchangeType.Topic, durable: true, cancellationToken: ct);
        return _channel;
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
