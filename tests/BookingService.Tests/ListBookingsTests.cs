using BookingService.Data;
using HotelBooking.Contracts.Booking;

namespace BookingService.Tests;

[Collection(BookingServiceCollection.Name)]
public class ListBookingsTests(BookingServiceFactory factory) : IAsyncLifetime
{
    // Creating the client starts the service, which applies migrations and seed data.
    private readonly Bookings.BookingsClient _client = factory.CreateGrpcClient();

    public Task InitializeAsync() => factory.DeleteBookingsAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Returns_only_the_users_own_bookings_newest_first()
    {
        await factory.AddBookingsAsync(
            factory.NewBooking(BookingStatus.Confirmed, userId: "user_a", createdAgo: TimeSpan.FromMinutes(60)),
            factory.NewBooking(BookingStatus.Held, userId: "user_a", createdAgo: TimeSpan.FromMinutes(5)),
            factory.NewBooking(BookingStatus.Confirmed, userId: "user_b", createdAgo: TimeSpan.FromMinutes(1)));

        var response = await _client.ListBookingsAsync(new ListBookingsRequest { ClerkUserId = "user_a" });

        Assert.Equal(["Held", "Confirmed"], response.Bookings.Select(b => b.Status));
        var first = response.Bookings[0];
        Assert.Equal(("Hotel Aurora", "Double Room"), (first.HotelName, first.RoomTypeName));
        Assert.Equal(("2026-11-10", "2026-11-12"), (first.CheckInDate, first.CheckOutDate));
    }

    [Fact]
    public async Task Shows_hold_past_its_expiry_as_expired_before_the_job_runs()
    {
        await factory.AddBookingsAsync(
            factory.NewBooking(BookingStatus.Held, userId: "user_a", createdAgo: TimeSpan.FromMinutes(31)));

        var response = await _client.ListBookingsAsync(new ListBookingsRequest { ClerkUserId = "user_a" });

        Assert.Equal("Expired", response.Bookings.Single().Status);
    }
}
