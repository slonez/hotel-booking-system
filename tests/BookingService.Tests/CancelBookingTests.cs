using BookingService.Data;
using Grpc.Core;
using HotelBooking.Contracts.Booking;
using Microsoft.EntityFrameworkCore;

namespace BookingService.Tests;

// Seed data: Seaside "Suite" (id 4) has 1 room. Test time is 2026-11-01 12:00 UTC.
[Collection(BookingServiceCollection.Name)]
public class CancelBookingTests(BookingServiceFactory factory) : IAsyncLifetime
{
    private const int SeasideSuite = 4;

    // Creating the client starts the service, which applies migrations and seed data.
    private readonly Bookings.BookingsClient _client = factory.CreateGrpcClient();

    public Task InitializeAsync() => factory.DeleteBookingsAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Cancels_confirmed_booking_with_full_refund_and_releases_the_room()
    {
        var booking = await AddSuiteBooking("user_a", BookingStatus.Confirmed);

        var response = await Cancel(booking.Id, "user_a");

        Assert.Equal(59800, response.RefundAmountCents);
        await using var db = factory.CreateDbContext();
        var saved = await db.Bookings.SingleAsync();
        Assert.Equal(BookingStatus.Cancelled, saved.Status);
        Assert.Equal(factory.Time.GetUtcNow(), saved.CancelledAt);
        Assert.Equal(59800, saved.RefundAmountCents);

        var search = await _client.SearchAvailabilityAsync(new SearchAvailabilityRequest
        {
            City = "Helsinki", CheckInDate = "2026-11-10", CheckOutDate = "2026-11-12"
        });
        Assert.Equal(1, search.RoomTypes.Single(rt => rt.RoomTypeId == SeasideSuite).AvailableRooms);
    }

    [Fact]
    public async Task Another_users_booking_is_not_found()
    {
        var booking = await AddSuiteBooking("user_a", BookingStatus.Confirmed);

        var ex = await Assert.ThrowsAsync<RpcException>(() => Cancel(booking.Id, "user_b"));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Theory]
    [InlineData(BookingStatus.Held)]
    [InlineData(BookingStatus.Expired)]
    [InlineData(BookingStatus.Cancelled)]
    public async Task Only_confirmed_bookings_can_be_cancelled(BookingStatus status)
    {
        var booking = await AddSuiteBooking("user_a", status);

        var ex = await Assert.ThrowsAsync<RpcException>(() => Cancel(booking.Id, "user_a"));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    private async Task<CancelBookingResponse> Cancel(Guid bookingId, string userId) =>
        await _client.CancelBookingAsync(new CancelBookingRequest
        {
            BookingId = bookingId.ToString(),
            ClerkUserId = userId
        });

    private async Task<Booking> AddSuiteBooking(string userId, BookingStatus status)
    {
        var booking = factory.NewBooking(status, roomTypeId: SeasideSuite, userId: userId, totalPriceCents: 59800);
        await factory.AddBookingsAsync(booking);
        return booking;
    }
}
