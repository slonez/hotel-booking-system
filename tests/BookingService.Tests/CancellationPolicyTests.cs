using BookingService.Data;

namespace BookingService.Tests;

public class CancellationPolicyTests
{
    private static readonly DateTimeOffset CheckIn = new(2026, 11, 10, 13, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(48 * 60, 10000)]   // two days before
    [InlineData(24 * 60, 10000)]   // exactly 24 h before: still full refund
    [InlineData(24 * 60 - 1, 5000)] // 23:59 before
    [InlineData(1, 5000)]           // one minute before
    public void Refund_depends_on_time_before_check_in(int minutesBefore, long expectedRefund)
    {
        var refund = CancellationPolicy.RefundAmount(10000, CheckIn, CheckIn.AddMinutes(-minutesBefore));

        Assert.Equal(expectedRefund, refund);
    }

    [Theory]
    [InlineData(0)]   // at check-in time
    [InlineData(60)]  // after check-in time
    public void Cancelling_is_not_allowed_from_check_in_time(int minutesAfter)
    {
        Assert.Null(CancellationPolicy.RefundAmount(10000, CheckIn, CheckIn.AddMinutes(minutesAfter)));
    }

    [Fact]
    public void Half_refund_rounds_down_to_whole_cents()
    {
        Assert.Equal(4999, CancellationPolicy.RefundAmount(9999, CheckIn, CheckIn.AddHours(-1)));
    }

    [Theory]
    [InlineData(2026, 11, 10, 13)] // winter time, UTC+2
    [InlineData(2026, 7, 10, 12)]  // summer time, UTC+3
    public void Check_in_moment_uses_hotel_local_time(int year, int month, int day, int expectedUtcHour)
    {
        var hotel = new Hotel
        {
            Name = "Hotel", Address = "Street 1", City = "Helsinki", ZipCode = "00100", Country = "Finland",
            CheckInTime = new TimeOnly(15, 0), TimeZone = "Europe/Helsinki"
        };

        var checkIn = CancellationPolicy.CheckInMoment(new DateOnly(year, month, day), hotel);

        Assert.Equal(new DateTimeOffset(year, month, day, expectedUtcHour, 0, 0, TimeSpan.Zero), checkIn);
    }
}
