using System.Globalization;
using HotelBooking.Contracts.Events;
using HotelBooking.Messaging;
using Microsoft.Extensions.DependencyInjection;
using PaymentService.Data;
using RabbitMQ.Client.Exceptions;

namespace PaymentService.Tests;

// These metrics are what the alerts in docker/prometheus/alerts.yml read.
[Collection(PaymentServiceCollection.Name)]
public class MetricsTests(PaymentServiceFactory factory) : IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => factory.ResetAsync();

    [Fact]
    public async Task Stuck_outbox_message_shows_in_the_metrics()
    {
        var publisher = factory.Services.GetRequiredService<OutboxPublisher<PaymentDbContext>>();
        await using (var db = factory.CreateDbContext())
        {
            // No queue is bound for this routing key, so publishing fails.
            db.OutboxMessages.Add(OutboxMessage.Create("test.metrics-no-queue",
                new PaymentSucceeded(Guid.CreateVersion7(), Guid.CreateVersion7(), 25800), factory.Time.GetUtcNow()));
            await db.SaveChangesAsync();
        }
        factory.Time.Advance(TimeSpan.FromMinutes(3));

        await Assert.ThrowsAsync<PublishReturnException>(() => publisher.PublishPendingAsync(CancellationToken.None));

        Assert.Equal(1, await GetMetricAsync("hotel_outbox_unpublished_messages"));
        Assert.Equal(180, await GetMetricAsync("hotel_outbox_oldest_message_age_seconds"));
    }

    [Fact]
    public async Task Empty_outbox_reports_zero_age()
    {
        var publisher = factory.Services.GetRequiredService<OutboxPublisher<PaymentDbContext>>();

        await publisher.PublishPendingAsync(CancellationToken.None);

        Assert.Equal(0, await GetMetricAsync("hotel_outbox_unpublished_messages"));
        Assert.Equal(0, await GetMetricAsync("hotel_outbox_oldest_message_age_seconds"));
    }

    [Fact]
    public async Task Failed_refund_shows_in_the_metrics_until_it_is_handled()
    {
        var processor = factory.Services.GetRequiredService<RefundProcessor>();
        var payment = new Payment
        {
            Id = Guid.CreateVersion7(),
            BookingId = Guid.CreateVersion7(),
            StripeSessionId = "cs_test_metrics",
            CheckoutUrl = "https://checkout.stripe.com/c/pay/cs_test_metrics",
            StripePaymentIntentId = "pi_test_metrics",
            AmountCents = 25800,
            Status = PaymentStatus.Succeeded
        };
        var refund = new Refund
        {
            Id = Guid.CreateVersion7(),
            PaymentId = payment.Id,
            AmountCents = 25800,
            Reason = RefundReason.Cancelled,
            Status = RefundStatus.Failed,
            CreatedAt = factory.Time.GetUtcNow()
        };
        await using (var db = factory.CreateDbContext())
        {
            db.Payments.Add(payment);
            db.Refunds.Add(refund);
            await db.SaveChangesAsync();
        }

        await processor.ProcessPendingAsync(CancellationToken.None);
        Assert.Equal(1, await GetMetricAsync("hotel_refunds_failed"));

        await factory.ResetAsync();
        await processor.ProcessPendingAsync(CancellationToken.None);
        Assert.Equal(0, await GetMetricAsync("hotel_refunds_failed"));
    }

    private async Task<double> GetMetricAsync(string name)
    {
        var metrics = await _client.GetStringAsync("/metrics");
        var line = Assert.Single(metrics.Split('\n'), l => l.StartsWith(name + " "));
        return double.Parse(line[(name.Length + 1)..], CultureInfo.InvariantCulture);
    }
}
