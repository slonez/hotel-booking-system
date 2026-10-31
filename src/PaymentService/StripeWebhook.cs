using HotelBooking.Contracts.Events;
using HotelBooking.Messaging;
using Microsoft.EntityFrameworkCore;
using PaymentService.Data;
using Stripe;
using Stripe.Checkout;

namespace PaymentService;

// Stripe calls this endpoint directly when a checkout session completes or expires.
public static class StripeWebhook
{
    public static async Task<IResult> HandleAsync(
        HttpRequest request,
        PaymentDbContext db,
        IConfiguration config,
        TimeProvider time,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        // The signature covers the exact bytes Stripe sent, so read the raw body.
        var json = await new StreamReader(request.Body).ReadToEndAsync(ct);

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(json, request.Headers["Stripe-Signature"], config["Stripe:WebhookSecret"]);
        }
        catch (StripeException ex)
        {
            logger.LogWarning(ex, "Rejected Stripe webhook with invalid signature");
            return Results.BadRequest();
        }

        if (stripeEvent.Data.Object is not Session session)
            return Results.Ok();

        PaymentStatus? newStatus = stripeEvent.Type switch
        {
            // Card payments are paid on completion; delayed methods complete first and succeed later.
            EventTypes.CheckoutSessionCompleted when session.PaymentStatus == "paid" => PaymentStatus.Succeeded,
            EventTypes.CheckoutSessionAsyncPaymentSucceeded => PaymentStatus.Succeeded,
            EventTypes.CheckoutSessionExpired => PaymentStatus.Expired,
            _ => null
        };
        if (newStatus is null)
            return Results.Ok();

        // The status change and the event are saved together: either both or neither (transactional outbox).
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Idempotent: only a Pending payment changes, so duplicate or late events do nothing.
        var updated = await db.Payments
            .Where(p => p.StripeSessionId == session.Id && p.Status == PaymentStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, newStatus.Value)
                .SetProperty(p => p.StripePaymentIntentId, session.PaymentIntentId), ct);

        if (updated == 0)
        {
            logger.LogInformation("Stripe event {EventId} ({Type}) changed nothing for session {SessionId}",
                stripeEvent.Id, stripeEvent.Type, session.Id);
            return Results.Ok();
        }

        if (newStatus == PaymentStatus.Succeeded)
        {
            var payment = await db.Payments.SingleAsync(p => p.StripeSessionId == session.Id, ct);
            db.OutboxMessages.Add(OutboxMessage.Create(PaymentSucceeded.RoutingKey,
                new PaymentSucceeded(payment.BookingId, payment.Id, payment.AmountCents), time.GetUtcNow()));
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);

        // Stripe retries until it gets a 2xx, so answer OK also when nothing changed.
        return Results.Ok();
    }
}
