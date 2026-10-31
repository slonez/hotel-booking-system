using BookingService.Data;
using HotelBooking.Contracts.Events;
using Microsoft.EntityFrameworkCore;

namespace BookingService;

// Confirms a booking when its payment succeeds. Messages can arrive more than once and late,
// so the handler is idempotent and checks availability again for late payments.
public class PaymentSucceededHandler(BookingDbContext db, TimeProvider time, ILogger<PaymentSucceededHandler> logger)
{
    public async Task HandleAsync(PaymentSucceeded message, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var booking = await db.Bookings.AsNoTracking().SingleOrDefaultAsync(b => b.Id == message.BookingId, ct);
        if (booking is null)
        {
            logger.LogWarning("Payment {PaymentId} succeeded for unknown booking {BookingId}", message.PaymentId, message.BookingId);
            return;
        }
        if (booking.Status is not (BookingStatus.Held or BookingStatus.Expired))
        {
            // Already confirmed: a duplicate message.
            logger.LogInformation("Booking {BookingId} is {Status}; ignoring payment {PaymentId}",
                booking.Id, booking.Status, message.PaymentId);
            return;
        }

        // Same lock as CreateBooking, so a late payment and a new booking cannot both take the last room.
        var roomType = await db.RoomTypes
            .FromSql($"""SELECT * FROM "RoomTypes" WHERE "Id" = {booking.RoomTypeId} FOR UPDATE""")
            .SingleAsync(ct);

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
                // TODO(Phase 5 step 4): publish RefundRequested (Reason = LatePayment).
                logger.LogWarning("Late payment {PaymentId} for booking {BookingId}, but no room is left",
                    message.PaymentId, booking.Id);
                return;
            }
        }

        // Atomic: the status condition protects against a concurrent duplicate message.
        await db.Bookings
            .Where(b => b.Id == booking.Id && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Expired))
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Confirmed), ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation("Booking {BookingId} confirmed by payment {PaymentId}", booking.Id, message.PaymentId);
    }
}
