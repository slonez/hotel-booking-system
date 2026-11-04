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

        if (!request.Headers.TryGetValue("Stripe-Signature", out var signature))
            return Results.BadRequest();

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(json, signature, config["Stripe:WebhookSecret"]);
        }
        catch (StripeException ex)
        {
            // Invalid signature, or an event API version that does not match the SDK.
            logger.LogWarning(ex, "Rejected Stripe webhook");
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

        if (!await PaymentOutcome.ApplyAsync(db, session, newStatus.Value, time, ct))
            logger.LogInformation("Stripe event {EventId} ({Type}) changed nothing for session {SessionId}",
                stripeEvent.Id, stripeEvent.Type, session.Id);

        // Stripe retries until it gets a 2xx, so answer OK also when nothing changed.
        return Results.Ok();
    }
}
