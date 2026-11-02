using System.Text.Json;
using BookingService.Data;
using HotelBooking.Contracts.Events;
using HotelBooking.Messaging;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

namespace BookingService.Tests;

[Collection(BookingServiceCollection.Name)]
public class PaymentSucceededConsumerTests(BookingServiceFactory factory) : IAsyncLifetime
{
    // Creating the client starts the service, which starts the consumer.
    private readonly HttpClient _http = factory.CreateClient();

    public Task InitializeAsync() => factory.DeleteBookingsAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Confirms_booking_from_message_published_to_rabbitmq()
    {
        var booking = factory.NewBooking(BookingStatus.Held);
        await factory.AddBookingsAsync(booking);

        // Published the same way as Payment Service's outbox publisher.
        await using var connection = await new ConnectionFactory { Uri = new Uri(factory.RabbitMqConnectionString) }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        // Declared like the consumer does, so the message waits in the queue even if the consumer has not started yet.
        await ConsumerQueue.DeclareAsync(channel, PaymentSucceededHandler.Queue, PaymentSucceeded.RoutingKey);
        await channel.BasicPublishAsync(Exchanges.Events, PaymentSucceeded.RoutingKey,
            JsonSerializer.SerializeToUtf8Bytes(new PaymentSucceeded(booking.Id, Guid.CreateVersion7(), booking.TotalPriceCents)));

        await WaitUntil(async () =>
        {
            await using var db = factory.CreateDbContext();
            return await db.Bookings.AnyAsync(b => b.Id == booking.Id && b.Status == BookingStatus.Confirmed);
        });
    }

    // Messages are handled in the background, so poll until the condition holds or time runs out.
    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if (await condition())
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met within 10 seconds.");
            await Task.Delay(100);
        }
    }
}
