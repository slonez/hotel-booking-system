using BookingService.Data;
using HotelBooking.Contracts.Booking;
using Microsoft.EntityFrameworkCore;

namespace BookingService.Tests;

public class ListBookingsTests(BookingServiceFactory factory) : IClassFixture<BookingServiceFactory>, IAsyncLifetime
{
    // Creating the client starts the service, which applies migrations and seed data.
    private readonly Bookings.BookingsClient _client = factory.CreateGrpcClient();

    public async Task InitializeAsync()
    {
        await using var db = factory.CreateDbContext();
        await db.Bookings.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Returns_only_the_users_own_bookings_newest_first()
    {
        await AddBookings(
            Booking("user_a", BookingStatus.Confirmed, createdMinutesAgo: 60),
            Booking("user_a", BookingStatus.Held, createdMinutesAgo: 5),
            Booking("user_b", BookingStatus.Confirmed, createdMinutesAgo: 1));

        var response = await _client.ListBookingsAsync(new ListBookingsRequest { ClerkUserId = "user_a" });

        Assert.Equal(["Held", "Confirmed"], response.Bookings.Select(b => b.Status));
        var first = response.Bookings[0];
        Assert.Equal(("Hotel Aurora", "Double Room"), (first.HotelName, first.RoomTypeName));
        Assert.Equal(("2026-11-10", "2026-11-12"), (first.CheckInDate, first.CheckOutDate));
    }

    [Fact]
    public async Task Shows_hold_past_its_expiry_as_expired_before_the_job_runs()
    {
        await AddBookings(Booking("user_a", BookingStatus.Held, createdMinutesAgo: 31));

        var response = await _client.ListBookingsAsync(new ListBookingsRequest { ClerkUserId = "user_a" });

        Assert.Equal("Expired", response.Bookings.Single().Status);
    }

    private Booking Booking(string userId, BookingStatus status, int createdMinutesAgo)
    {
        var createdAt = factory.Time.GetUtcNow().AddMinutes(-createdMinutesAgo);
        return new Booking
        {
            Id = Guid.CreateVersion7(),
            RoomTypeId = 2,
            ClerkUserId = userId,
            Email = $"{userId}@example.com",
            Status = status,
            CheckInDate = new DateOnly(2026, 11, 10),
            CheckOutDate = new DateOnly(2026, 11, 12),
            TotalPriceCents = 25800,
            ExpiresAt = createdAt.AddMinutes(30),
            CreatedAt = createdAt
        };
    }

    private async Task AddBookings(params Booking[] bookings)
    {
        await using var db = factory.CreateDbContext();
        db.Bookings.AddRange(bookings);
        await db.SaveChangesAsync();
    }
}
