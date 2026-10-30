using BookingService.Data;
using Grpc.Core;
using HotelBooking.Contracts.Booking;
using Microsoft.EntityFrameworkCore;

namespace BookingService.Tests;

// Seed data: Hotel Aurora "Double Room" (id 2) has 3 rooms at 129 EUR, Seaside "Suite" (id 4) has 1 room.
[Collection(BookingServiceCollection.Name)]
public class CreateBookingTests(BookingServiceFactory factory) : IAsyncLifetime
{
    private const int AuroraDouble = 2;
    private const int SeasideSuite = 4;

    // Creating the client starts the service, which applies migrations and seed data.
    private readonly Bookings.BookingsClient _client = factory.CreateGrpcClient();

    public Task InitializeAsync() => factory.DeleteBookingsAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Creates_held_booking_with_total_price_and_30_minute_hold()
    {
        var response = await Book(AuroraDouble, "2026-11-10", "2026-11-12");

        await using var db = factory.CreateDbContext();
        var booking = await db.Bookings.SingleAsync();
        Assert.Equal(response.BookingId, booking.Id.ToString());
        Assert.Equal(BookingStatus.Held, booking.Status);
        Assert.Equal("user_1", booking.ClerkUserId);
        Assert.Equal(2 * 12900, booking.TotalPriceCents);
        Assert.Equal(factory.Time.GetUtcNow().AddMinutes(30), booking.ExpiresAt);
        Assert.Equal(booking.ExpiresAt, response.ExpiresAt.ToDateTimeOffset());
    }

    [Fact]
    public async Task Rejects_booking_when_all_rooms_are_taken()
    {
        await Book(SeasideSuite, "2026-11-10", "2026-11-12");

        var ex = await Assert.ThrowsAsync<RpcException>(() => Book(SeasideSuite, "2026-11-11", "2026-11-13"));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task Rejects_unknown_room_type()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() => Book(999, "2026-11-10", "2026-11-12"));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task Concurrent_requests_for_the_last_room_create_only_one_booking()
    {
        var attempts = Enumerable.Range(0, 10)
            .Select(i => TryBook(SeasideSuite, "2026-11-10", "2026-11-12", $"user_{i}"));

        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(r => r == StatusCode.OK));
        Assert.Equal(9, results.Count(r => r == StatusCode.FailedPrecondition));

        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.Bookings.CountAsync());
    }

    private async Task<CreateBookingResponse> Book(int roomTypeId, string checkIn, string checkOut, string userId = "user_1") =>
        await _client.CreateBookingAsync(new CreateBookingRequest
        {
            RoomTypeId = roomTypeId,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            ClerkUserId = userId,
            Email = $"{userId}@example.com"
        });

    private async Task<StatusCode> TryBook(int roomTypeId, string checkIn, string checkOut, string userId)
    {
        try
        {
            await Book(roomTypeId, checkIn, checkOut, userId);
            return StatusCode.OK;
        }
        catch (RpcException ex)
        {
            return ex.StatusCode;
        }
    }
}
