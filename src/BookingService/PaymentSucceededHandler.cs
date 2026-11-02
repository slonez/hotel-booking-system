using BookingService.Data;
using HotelBooking.Contracts.Events;
using HotelBooking.Messaging;
using Microsoft.EntityFrameworkCore;

namespace BookingService;

// Confirms a booking when its payment succeeds. Messages can arrive more than once and late,
// so the handler is idempotent and checks availability again for late payments.
public class PaymentSucceededHandler(BookingDbContext db, TimeProvider time, ILogger<PaymentSucceededHandler> logger)
    : IEventHandler<PaymentSucceeded>
{
    public const string Queue = "booking.payment-succeeded";

    public async Task HandleAsync(PaymentSucceeded message, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var booking = await db.Bookings.AsNoTracking()
            .Include(b => b.RoomType).ThenInclude(rt => rt.Hotel)
            .SingleOrDefaultAsync(b => b.Id == message.BookingId, ct);
        if (booking is null)
        {
            logger.LogWarning("Payment {PaymentId} succeeded for unknown booking {BookingId}", message.PaymentId, message.BookingId);
            return;
        }
        if (booking.Status is not (BookingStatus.Held or BookingStatus.Expired) || booking.RefundAmountCents is not null)
        {
            // Already confirmed or refunded: a duplicate message.
            logger.LogInformation("Booking {BookingId} is {Status}; ignoring payment {PaymentId}",
                booking.Id, booking.Status, message.PaymentId);
            return;
        }

        // Same lock as CreateBooking, so a late payment and a new booking cannot both take the last room.
        var roomType = await db.RoomTypes
            .FromSql($"""SELECT * FROM "RoomTypes" WHERE "Id" = {booking.RoomTypeId} FOR UPDATE""")
            .SingleAsync(ct);

        // Read after the lock: while waiting for it, the hold may have expired and another booking taken the room.
        var now = time.GetUtcNow();
        var holdIsValid = booking.Status == BookingStatus.Held && booking.ExpiresAt > now;
        if (!holdIsValid)
        {
            // Late payment (FR-12): the room was released when the hold expired, so check again.
            var others = await db.Bookings
                .Where(b => b.Id != booking.Id && b.RoomTypeId == booking.RoomTypeId
                    && b.CheckInDate < booking.CheckOutDate && b.CheckOutDate > booking.CheckInDate)
                .Where(Availability.TakesRoom(now))
                .ToListAsync(ct);
            if (Availability.MaxRoomsTaken(others, booking.CheckInDate, booking.CheckOutDate) >= roomType.TotalRooms)
            {
                // The customer gets the full payment back. RefundAmountCents marks this, so a duplicate
                // message cannot confirm the booking later, when a room may have become free.
                var marked = await db.Bookings
                    .Where(b => b.Id == booking.Id && b.RefundAmountCents == null
                        && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Expired))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(b => b.Status, BookingStatus.Expired)
                        .SetProperty(b => b.RefundAmountCents, message.AmountCents), ct);
                if (marked > 0)
                {
                    logger.LogWarning("Late payment {PaymentId} for booking {BookingId}, but no room is left; refunding",
                        message.PaymentId, booking.Id);
                    db.OutboxMessages.Add(OutboxMessage.Create(RefundRequested.RoutingKey,
                        BookingsService.RefundRequestedFor(booking, message.AmountCents, RefundReason.LatePayment), now));
                    await db.SaveChangesAsync(ct);
                }
                await transaction.CommitAsync(ct);
                return;
            }
        }

        // Atomic: the status condition protects against a concurrent duplicate message.
        var confirmed = await db.Bookings
            .Where(b => b.Id == booking.Id && b.RefundAmountCents == null
                && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Expired))
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Confirmed), ct);
        if (confirmed > 0)
        {
            logger.LogInformation("Booking {BookingId} confirmed by payment {PaymentId}", booking.Id, message.PaymentId);
            db.OutboxMessages.Add(OutboxMessage.Create(BookingConfirmed.RoutingKey,
                new BookingConfirmed(booking.Id, booking.Email, booking.RoomType.Hotel.Name, booking.RoomType.Name,
                    booking.CheckInDate, booking.CheckOutDate, booking.TotalPriceCents), now));
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
}
