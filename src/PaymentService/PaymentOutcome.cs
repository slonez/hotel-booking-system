using HotelBooking.Contracts.Events;
using HotelBooking.Messaging;
using Microsoft.EntityFrameworkCore;
using PaymentService.Data;
using Stripe.Checkout;

namespace PaymentService;

// Applies what Stripe reports about a checkout session. Webhooks and PaymentReconciliationJob both
// come here, so it does not matter which one sees the result first or how many times.
public static class PaymentOutcome
{
    // Returns false if nothing changed.
    public static async Task<bool> ApplyAsync(
        PaymentDbContext db, Session session, PaymentStatus newStatus, TimeProvider time, CancellationToken ct)
    {
        // The status change and the event are saved together: either both or neither (transactional outbox).
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Idempotent: only a Pending payment changes, so duplicate or late results do nothing.
        var updated = await db.Payments
            .Where(p => p.StripeSessionId == session.Id && p.Status == PaymentStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, newStatus)
                .SetProperty(p => p.StripePaymentIntentId, session.PaymentIntentId), ct);
        if (updated == 0)
            return false;

        if (newStatus == PaymentStatus.Succeeded)
        {
            var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.StripeSessionId == session.Id, ct);
            db.OutboxMessages.Add(OutboxMessage.Create(PaymentSucceeded.RoutingKey,
                new PaymentSucceeded(payment.BookingId, payment.Id, payment.AmountCents), time.GetUtcNow()));
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return true;
    }
}
