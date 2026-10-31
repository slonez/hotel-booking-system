using System.Text.Json;
using HotelBooking.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentService.Data;
using RabbitMQ.Client;

namespace PaymentService.Tests;

[Collection(PaymentServiceCollection.Name)]
public class OutboxPublisherTests(PaymentServiceFactory factory) : IAsyncLifetime
{
    private const string TestQueue = "test.payment-succeeded";

    // Resolving the publisher starts the service, which applies migrations.
    private readonly OutboxPublisher _publisher = factory.Services.GetRequiredService<OutboxPublisher>();

    private IConnection _connection = null!;
    private IChannel _channel = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();

        // Stands in for Booking Service: a queue bound to the exchange, so published messages can be read back.
        _connection = await new ConnectionFactory { Uri = new Uri(factory.RabbitMqConnectionString) }.CreateConnectionAsync();
        _channel = await _connection.CreateChannelAsync();
        await _channel.ExchangeDeclareAsync(Exchanges.Events, ExchangeType.Topic, durable: true);
        await _channel.QueueDeclareAsync(TestQueue, durable: true, exclusive: false, autoDelete: false);
        await _channel.QueuePurgeAsync(TestQueue);
        await _channel.QueueBindAsync(TestQueue, Exchanges.Events, PaymentSucceeded.RoutingKey);
    }

    public async Task DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Publishes_pending_messages_and_marks_them_published()
    {
        var paymentSucceeded = new PaymentSucceeded(Guid.CreateVersion7(), Guid.CreateVersion7(), 25800);
        var message = OutboxMessage.Create(PaymentSucceeded.RoutingKey, paymentSucceeded, factory.Time.GetUtcNow());
        await using (var db = factory.CreateDbContext())
        {
            db.OutboxMessages.Add(message);
            await db.SaveChangesAsync();
        }

        var published = await _publisher.PublishPendingAsync(CancellationToken.None);

        Assert.Equal(1, published);
        var delivered = await _channel.BasicGetAsync(TestQueue, autoAck: true);
        Assert.NotNull(delivered);
        Assert.Equal(message.Id.ToString(), delivered.BasicProperties.MessageId);
        Assert.Equal(paymentSucceeded, JsonSerializer.Deserialize<PaymentSucceeded>(delivered.Body.Span));

        await using var check = factory.CreateDbContext();
        Assert.Equal(factory.Time.GetUtcNow(), (await check.OutboxMessages.SingleAsync()).PublishedAt);
    }

    [Fact]
    public async Task Does_not_publish_a_message_twice()
    {
        await using (var db = factory.CreateDbContext())
        {
            db.OutboxMessages.Add(OutboxMessage.Create(PaymentSucceeded.RoutingKey,
                new PaymentSucceeded(Guid.CreateVersion7(), Guid.CreateVersion7(), 25800), factory.Time.GetUtcNow()));
            await db.SaveChangesAsync();
        }

        await _publisher.PublishPendingAsync(CancellationToken.None);
        var secondRun = await _publisher.PublishPendingAsync(CancellationToken.None);

        Assert.Equal(0, secondRun);
        Assert.Equal(1u, await _channel.MessageCountAsync(TestQueue));
    }
}
