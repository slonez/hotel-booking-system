using Microsoft.EntityFrameworkCore;
using PaymentService.Data;
using Prometheus;
using Stripe;
using Stripe.Checkout;

namespace PaymentService;

// Asks Stripe about payments still Pending after webhooks should have arrived, so a lost webhook
// cannot leave a customer charged without a booking (NFR-3). Sessions end within about 30 minutes,
// so few payments are ever waiting.
public class PaymentReconciliationJob(IServiceScopeFactory scopes, TimeProvider time, ILogger<PaymentReconciliationJob> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    // Gives the webhook time to arrive first. Arriving together would be harmless, only wasteful.
    public static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(10);

    private static readonly Counter Reconciled = Metrics.CreateCounter(
        "hotel_payments_reconciled_total", "Payments updated by reconciliation because their webhook never arrived.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Reconciling payments failed");
            }
        }
    }

    public async Task<int> ReconcileAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();

        var cutoff = time.GetUtcNow() - MinimumAge;
        var pending = await db.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Pending && p.CreatedAt <= cutoff)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);

        var changed = 0;
        foreach (var payment in pending)
        {
            try
            {
                var session = await sessions.GetAsync(payment.StripeSessionId, cancellationToken: ct);
                PaymentStatus? newStatus = (session.Status, session.PaymentStatus) switch
                {
                    ("complete", "paid") => PaymentStatus.Succeeded,
                    ("expired", _) => PaymentStatus.Expired,
                    // Still open, or a delayed payment method has not finished.
                    _ => null
                };
                if (newStatus is null || !await PaymentOutcome.ApplyAsync(db, session, newStatus.Value, time, ct))
                    continue;

                changed++;
                Reconciled.Inc();
                logger.LogWarning("No webhook arrived for payment {PaymentId} of booking {BookingId}; reconciled it to {Status}",
                    payment.Id, payment.BookingId, newStatus);
            }
            catch (StripeException ex)
            {
                // The next tick tries again. One failure must not stop the other payments.
                logger.LogWarning(ex, "Could not check payment {PaymentId} with Stripe; retrying", payment.Id);
            }
        }
        return changed;
    }
}
