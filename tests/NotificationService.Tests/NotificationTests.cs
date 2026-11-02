using System.Text.Json;
using HotelBooking.Contracts.Events;
using HotelBooking.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Data;
using RabbitMQ.Client;

namespace NotificationService.Tests;

public class NotificationTests(NotificationServiceFactory factory) : IClassFixture<NotificationServiceFactory>, IAsyncLifetime
{
    private const string Email = "customer@example.com";

    // Resolving the services starts the service, which applies migrations and starts the consumers.
    private readonly IServiceProvider _services = factory.Services;

    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Sends_confirmation_email_from_message_published_to_rabbitmq()
    {
        var message = Confirmed();

        await PublishAsync(BookingConfirmedHandler.Queue, BookingConfirmed.RoutingKey, message);

        var email = await WaitForSingleEmailAsync();
        Assert.Equal(Email, email.To);
        Assert.Equal("Your booking at Hotel Aurora is confirmed", email.Subject);
        Assert.Contains("Double Room", email.Text);
        Assert.Contains("2026-11-10", email.Text);
        Assert.Contains("2026-11-12", email.Text);
        Assert.Contains("258.00 EUR", email.Text);
    }

    [Theory]
    [InlineData(RefundReason.Cancelled, "Your booking at Hotel Aurora is cancelled")]
    [InlineData(RefundReason.LatePayment, "We could not complete your booking at Hotel Aurora")]
    public async Task Refund_reason_decides_the_email(RefundReason reason, string subject)
    {
        await PublishAsync(RefundRequestedHandler.Queue, RefundRequested.RoutingKey, Refund(reason));

        var email = await WaitForSingleEmailAsync();
        Assert.Equal(Email, email.To);
        Assert.Equal(subject, email.Subject);
        Assert.Contains("129.00 EUR", email.Text);
    }

    [Fact]
    public async Task Duplicate_message_sends_one_email()
    {
        var message = Confirmed();

        await HandleConfirmedAsync(message);
        await HandleConfirmedAsync(message);

        Assert.Single(await factory.GetEmailsAsync());
    }

    [Fact]
    public async Task Confirmation_and_cancellation_of_the_same_booking_both_send_an_email()
    {
        var confirmed = Confirmed();

        await HandleConfirmedAsync(confirmed);
        await using (var scope = _services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<RefundRequestedHandler>()
                .HandleAsync(Refund(RefundReason.Cancelled) with { BookingId = confirmed.BookingId }, CancellationToken.None);

        Assert.Equal(2, (await factory.GetEmailsAsync()).Count);
    }

    [Fact]
    public async Task Failed_sending_leaves_no_log_row_so_a_retry_sends_the_email()
    {
        var message = Confirmed();
        var unreachable = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:From"] = "no-reply@hotel-booking.example",
            ["Smtp:Host"] = "localhost",
            // Nothing listens on port 1.
            ["Smtp:Port"] = "1"
        }).Build();

        await using (var db = factory.CreateDbContext())
        {
            var failing = new BookingConfirmedHandler(new NotificationSender(
                db, new EmailSender(unreachable), TimeProvider.System, NullLogger<NotificationSender>.Instance));
            await Assert.ThrowsAnyAsync<Exception>(() => failing.HandleAsync(message, CancellationToken.None));
            Assert.False(await db.Notifications.AnyAsync(n => n.BookingId == message.BookingId));
        }

        await HandleConfirmedAsync(message);

        Assert.Single(await factory.GetEmailsAsync());
    }

    private static BookingConfirmed Confirmed() => new(Guid.CreateVersion7(), Email, "Hotel Aurora", "Double Room",
        new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 12), 25800);

    private static RefundRequested Refund(RefundReason reason) =>
        new(Guid.CreateVersion7(), Email, "Hotel Aurora", "Double Room", 12900, reason);

    private async Task HandleConfirmedAsync(BookingConfirmed message)
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<BookingConfirmedHandler>().HandleAsync(message, CancellationToken.None);
    }

    private async Task PublishAsync<T>(string queue, string routingKey, T message)
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(factory.RabbitMqConnectionString) }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        // Declared like the consumer does, so the message waits in the queue even if the consumer has not started yet.
        await ConsumerQueue.DeclareAsync(channel, queue, routingKey);
        await channel.BasicPublishAsync(Exchanges.Events, routingKey, JsonSerializer.SerializeToUtf8Bytes(message));
    }

    // Messages are handled in the background, so poll until the email arrives or time runs out.
    private async Task<ReceivedEmail> WaitForSingleEmailAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var emails = await factory.GetEmailsAsync();
            if (emails.Count > 0)
                return Assert.Single(emails);
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("No email arrived within 10 seconds.");
            await Task.Delay(100);
        }
    }
}
