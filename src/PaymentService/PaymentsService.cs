using Grpc.Core;
using HotelBooking.Contracts.Payment;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PaymentService.Data;
using Stripe;
using Stripe.Checkout;

namespace PaymentService;

public class PaymentsService(
    PaymentDbContext db,
    SessionService sessions,
    TimeProvider time,
    IConfiguration config,
    ILogger<PaymentsService> logger) : Payments.PaymentsBase
{
    // Stripe's minimum. The hold can expire before the session; late payment handles that (FR-12).
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);

    public override async Task<CreateCheckoutSessionResponse> CreateCheckoutSession(
        CreateCheckoutSessionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.BookingId, out var bookingId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid booking ID."));
        if (request.AmountCents <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Amount must be positive."));

        var ct = context.CancellationToken;
        var existing = await FindPayment(bookingId, ct);
        if (existing is not null)
            return Respond(existing);

        var now = time.GetUtcNow();
        var session = await CreateStripeSession(new SessionCreateOptions
        {
            Mode = "payment",
            ClientReferenceId = bookingId.ToString(),
            CustomerEmail = request.CustomerEmail,
            ExpiresAt = now.Add(SessionLifetime).UtcDateTime,
            SuccessUrl = config["Stripe:SuccessUrl"],
            CancelUrl = config["Stripe:CancelUrl"],
            LineItems =
            [
                new SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = "eur",
                        UnitAmount = request.AmountCents,
                        ProductData = new SessionLineItemPriceDataProductDataOptions { Name = request.Description }
                    }
                }
            ]
        }, ct);

        var payment = new Payment
        {
            Id = Guid.CreateVersion7(),
            BookingId = bookingId,
            StripeSessionId = session.Id,
            CheckoutUrl = session.Url,
            AmountCents = request.AmountCents,
            Status = PaymentStatus.Pending,
            CreatedAt = now
        };
        db.Payments.Add(payment);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent call for the same booking saved first. The customer only ever gets
            // the saved session's URL, so our session is never shown and expires unpaid.
            db.ChangeTracker.Clear();
            return Respond((await FindPayment(bookingId, ct))!);
        }

        return Respond(payment);
    }

    private async Task<Session> CreateStripeSession(SessionCreateOptions options, CancellationToken ct)
    {
        try
        {
            return await sessions.CreateAsync(options, cancellationToken: ct);
        }
        catch (StripeException ex)
        {
            // Details may reveal our Stripe setup, so the caller only gets a generic error.
            logger.LogError(ex, "Creating Stripe checkout session for booking {BookingId} failed", options.ClientReferenceId);
            throw new RpcException(new Status(StatusCode.Unavailable, "Payment provider is unavailable."));
        }
    }

    private Task<Payment?> FindPayment(Guid bookingId, CancellationToken ct) =>
        db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.BookingId == bookingId, ct);

    private static CreateCheckoutSessionResponse Respond(Payment payment) =>
        payment.Status == PaymentStatus.Pending
            ? new CreateCheckoutSessionResponse { CheckoutUrl = payment.CheckoutUrl }
            : throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Payment for this booking is already {payment.Status.ToString().ToLowerInvariant()}."));
}
