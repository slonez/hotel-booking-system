using System.Globalization;
using System.Text.Json;
using HotelBooking.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentService.Data;
using Stripe.Checkout;

namespace PaymentService.Tests;

[Collection(PaymentServiceCollection.Name)]
public class PaymentReconciliationTests(PaymentServiceFactory factory) : IAsyncLifetime
{
    private static readonly TimeSpan OldEnough = PaymentReconciliationJob.MinimumAge + TimeSpan.FromMinutes(1);

    // Resolving the job starts the service, which applies migrations.
    private readonly PaymentReconciliationJob _job = factory.Services.GetRequiredService<PaymentReconciliationJob>();

    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Paid_session_without_a_webhook_succeeds_and_publishes_the_event()
    {
        var payment = await AddPaymentAsync("cs_paid", OldEnough);
        factory.Sessions.Existing["cs_paid"] = Session("cs_paid", "complete", "paid");

        Assert.Equal(1, await _job.ReconcileAsync(CancellationToken.None));

        await using var db = factory.CreateDbContext();
        var saved = await db.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.Succeeded, saved.Status);
        Assert.Equal("pi_cs_paid", saved.StripePaymentIntentId);
        var message = await db.OutboxMessages.SingleAsync();
        Assert.Equal(PaymentSucceeded.RoutingKey, message.Type);
        Assert.Equal(new PaymentSucceeded(payment.BookingId, payment.Id, payment.AmountCents),
            JsonSerializer.Deserialize<PaymentSucceeded>(message.Payload));
    }

    [Fact]
    public async Task Expired_session_expires_the_payment_without_an_event()
    {
        await AddPaymentAsync("cs_expired", OldEnough);
        factory.Sessions.Existing["cs_expired"] = Session("cs_expired", "expired", "unpaid");

        await _job.ReconcileAsync(CancellationToken.None);

        await using var db = factory.CreateDbContext();
        Assert.Equal(PaymentStatus.Expired, (await db.Payments.SingleAsync()).Status);
        Assert.False(await db.OutboxMessages.AnyAsync());
    }

    [Theory]
    [InlineData("open", "unpaid")]
    // A delayed payment method has completed the session but not paid yet.
    [InlineData("complete", "unpaid")]
    public async Task Unfinished_session_stays_pending(string status, string paymentStatus)
    {
        await AddPaymentAsync("cs_open", OldEnough);
        factory.Sessions.Existing["cs_open"] = Session("cs_open", status, paymentStatus);

        Assert.Equal(0, await _job.ReconcileAsync(CancellationToken.None));

        await using var db = factory.CreateDbContext();
        Assert.Equal(PaymentStatus.Pending, (await db.Payments.SingleAsync()).Status);
    }

    [Fact]
    public async Task Recent_payment_is_left_for_its_webhook()
    {
        await AddPaymentAsync("cs_recent", PaymentReconciliationJob.MinimumAge - TimeSpan.FromMinutes(1));

        await _job.ReconcileAsync(CancellationToken.None);

        Assert.Empty(factory.Sessions.Fetched);
    }

    [Fact]
    public async Task Finished_payment_is_not_asked_about_again()
    {
        await AddPaymentAsync("cs_twice", OldEnough);
        factory.Sessions.Existing["cs_twice"] = Session("cs_twice", "complete", "paid");

        await _job.ReconcileAsync(CancellationToken.None);
        Assert.Equal(0, await _job.ReconcileAsync(CancellationToken.None));

        Assert.Single(factory.Sessions.Fetched);
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Webhook_arriving_after_reconciliation_publishes_nothing_more()
    {
        await AddPaymentAsync("cs_late", OldEnough);
        var session = Session("cs_late", "complete", "paid");
        factory.Sessions.Existing["cs_late"] = session;
        await _job.ReconcileAsync(CancellationToken.None);

        await using var db = factory.CreateDbContext();
        Assert.False(await PaymentOutcome.ApplyAsync(db, session, PaymentStatus.Succeeded, factory.Time, CancellationToken.None));

        Assert.Single(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Stripe_failure_on_one_payment_does_not_stop_the_others()
    {
        await AddPaymentAsync("cs_fails", OldEnough + TimeSpan.FromMinutes(1));
        await AddPaymentAsync("cs_works", OldEnough);
        factory.Sessions.Existing["cs_fails"] = null;
        factory.Sessions.Existing["cs_works"] = Session("cs_works", "complete", "paid");

        Assert.Equal(1, await _job.ReconcileAsync(CancellationToken.None));

        await using var db = factory.CreateDbContext();
        Assert.Equal(PaymentStatus.Pending, (await db.Payments.SingleAsync(p => p.StripeSessionId == "cs_fails")).Status);
        Assert.Equal(PaymentStatus.Succeeded, (await db.Payments.SingleAsync(p => p.StripeSessionId == "cs_works")).Status);
    }

    [Fact]
    public async Task Each_fixed_payment_increases_the_metric_behind_the_alert()
    {
        var client = factory.CreateClient();
        var before = await GetReconciledAsync(client);
        await AddPaymentAsync("cs_metric", OldEnough);
        factory.Sessions.Existing["cs_metric"] = Session("cs_metric", "complete", "paid");

        await _job.ReconcileAsync(CancellationToken.None);

        Assert.Equal(before + 1, await GetReconciledAsync(client));
    }

    private static async Task<double> GetReconciledAsync(HttpClient client)
    {
        var metrics = await client.GetStringAsync("/metrics");
        var line = Assert.Single(metrics.Split('\n'), l => l.StartsWith("hotel_payments_reconciled_total "));
        return double.Parse(line.Split(' ')[1], CultureInfo.InvariantCulture);
    }

    private static Session Session(string id, string status, string paymentStatus) =>
        new() { Id = id, Status = status, PaymentStatus = paymentStatus, PaymentIntentId = $"pi_{id}" };

    private async Task<Payment> AddPaymentAsync(string sessionId, TimeSpan age)
    {
        var payment = new Payment
        {
            Id = Guid.CreateVersion7(),
            BookingId = Guid.CreateVersion7(),
            StripeSessionId = sessionId,
            CheckoutUrl = $"https://checkout.stripe.com/c/pay/{sessionId}",
            AmountCents = 25800,
            Status = PaymentStatus.Pending,
            CreatedAt = factory.Time.GetUtcNow() - age
        };
        await using var db = factory.CreateDbContext();
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }
}
