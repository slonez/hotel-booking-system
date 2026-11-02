using Microsoft.EntityFrameworkCore;
using PaymentService.Data;
using Stripe;

namespace PaymentService;

// Executes pending refunds through Stripe. A failed attempt is retried on the next tick;
// after MaxAttempts the refund is marked Failed for manual handling.
public class RefundProcessor(IServiceScopeFactory scopes, RefundService stripeRefunds, TimeProvider time, ILogger<RefundProcessor> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    public const int MaxAttempts = 5;
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Processing refunds failed");
            }
        }
    }

    public async Task<int> ProcessPendingAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

        var refunds = await db.Refunds
            .Include(r => r.Payment)
            .Where(r => r.Status == RefundStatus.Pending)
            .OrderBy(r => r.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var refund in refunds)
        {
            try
            {
                // Same idempotency key on every attempt: if an earlier attempt reached Stripe but
                // the response was lost, Stripe returns that refund instead of creating a new one.
                var stripeRefund = await stripeRefunds.CreateAsync(new RefundCreateOptions
                {
                    PaymentIntent = refund.Payment.StripePaymentIntentId,
                    Amount = refund.AmountCents
                }, new RequestOptions { IdempotencyKey = refund.Id.ToString() }, ct);

                refund.Status = RefundStatus.Succeeded;
                refund.StripeRefundId = stripeRefund.Id;
            }
            catch (StripeException ex)
            {
                refund.Attempts++;
                if (refund.Attempts >= MaxAttempts)
                {
                    refund.Status = RefundStatus.Failed;
                    logger.LogError(ex, "Refund {RefundId} failed {Attempts} times; needs manual handling", refund.Id, refund.Attempts);
                }
                else
                {
                    logger.LogWarning(ex, "Refund {RefundId} attempt {Attempts} failed; retrying", refund.Id, refund.Attempts);
                }
            }
            // Saved one by one, so a crash does not lose the result of earlier refunds.
            await db.SaveChangesAsync(ct);
        }
        return refunds.Count;
    }
}
