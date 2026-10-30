using BookingService.Data;

namespace BookingService;

public static class CancellationPolicy
{
    private static readonly TimeSpan FullRefundNotice = TimeSpan.FromHours(24);

    // Returns the refund in cents, or null if check-in time has passed and cancelling is no longer allowed.
    // At least 24 h before check-in: 100%. Less than that: 50% (rounded down).
    public static long? RefundAmount(long totalPriceCents, DateTimeOffset checkIn, DateTimeOffset now)
    {
        if (now >= checkIn)
            return null;

        return checkIn - now >= FullRefundNotice ? totalPriceCents : totalPriceCents / 2;
    }

    // The check-in moment is the hotel's local check-in time on the check-in date.
    public static DateTimeOffset CheckInMoment(DateOnly checkInDate, Hotel hotel)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(hotel.TimeZone);
        var local = checkInDate.ToDateTime(hotel.CheckInTime);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local));
    }
}
