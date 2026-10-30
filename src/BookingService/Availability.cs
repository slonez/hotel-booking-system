using System.Linq.Expressions;
using BookingService.Data;

namespace BookingService;

public static class Availability
{
    // ExpiresAt is the source of truth: a hold past its expiry no longer takes a room,
    // even if the expiry job has not updated its status yet.
    public static Expression<Func<Booking, bool>> TakesRoom(DateTimeOffset now) =>
        b => b.Status == BookingStatus.Confirmed
             || (b.Status == BookingStatus.Held && b.ExpiresAt > now);

    // Bookings can overlap the stay on different nights (e.g. 10-11 and 11-12),
    // so rooms are counted night by night and the busiest night decides.
    public static int MaxRoomsTaken(IEnumerable<Booking> bookings, DateOnly checkIn, DateOnly checkOut)
    {
        var max = 0;
        for (var night = checkIn; night < checkOut; night = night.AddDays(1))
        {
            var taken = bookings.Count(b => b.CheckInDate <= night && night < b.CheckOutDate);
            max = Math.Max(max, taken);
        }
        return max;
    }
}
