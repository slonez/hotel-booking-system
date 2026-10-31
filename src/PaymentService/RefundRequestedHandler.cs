using HotelBooking.Contracts.Events;
using HotelBooking.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PaymentService.Data;

namespace PaymentService;

// Saves a refund to be executed by RefundProcessor. Calling Stripe happens there, so a slow or
// failing Stripe does not block the queue, and the number of attempts can be limited.
public class RefundRequestedHandler(PaymentDbContext db, TimeProvider time, ILogger<RefundRequestedHandler> logger)
    : IEventHandler<RefundRequested>
{
    public const string Queue = "payment.refund-requested";

    public async Task HandleAsync(RefundRequested message, CancellationToken ct)
    {
        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.BookingId == message.BookingId, ct);
        if (payment is not { Status: PaymentStatus.Succeeded })
        {
            // Booking only requests refunds for paid bookings, so this is a bug or bad data.
            logger.LogError("Refund requested for booking {BookingId} without a succeeded payment", message.BookingId);
            return;
        }
        if (message.AmountCents <= 0 || message.AmountCents > payment.AmountCents)
        {
            logger.LogError("Refund of {Amount} for booking {BookingId} is not within the paid {Paid}",
                message.AmountCents, message.BookingId, payment.AmountCents);
            return;
        }

        db.Refunds.Add(new Refund
        {
            Id = Guid.CreateVersion7(),
            PaymentId = payment.Id,
            AmountCents = message.AmountCents,
            Reason = message.Reason,
            Status = RefundStatus.Pending,
            CreatedAt = time.GetUtcNow()
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // One refund per payment: a duplicate message (or a repeated late payment event) changes nothing.
            logger.LogInformation("Refund for payment {PaymentId} already exists; ignoring duplicate", payment.Id);
        }
    }
}
