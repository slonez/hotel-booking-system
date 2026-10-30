using BookingService.Data;
using Grpc.Core;
using HotelBooking.Contracts.Booking;

namespace BookingService.Tests;

// Seed data: Hotel Aurora (Helsinki) has room type 2 "Double Room" with 3 rooms.
[Collection(BookingServiceCollection.Name)]
public class SearchAvailabilityTests(BookingServiceFactory factory) : IAsyncLifetime
{
    private const int AuroraDouble = 2;

    // Creating the client starts the service, which applies migrations and seed data.
    private readonly Bookings.BookingsClient _client = factory.CreateGrpcClient();

    public Task InitializeAsync() => factory.DeleteBookingsAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Returns_room_types_in_city_with_total_price()
    {
        var response = await Search("helsinki", "2026-11-10", "2026-11-12");

        Assert.Equal(4, response.RoomTypes.Count);
        Assert.DoesNotContain(response.RoomTypes, rt => rt.HotelName == "Lakeview Hotel");

        var auroraDouble = AuroraDoubleRoom(response);
        Assert.Equal(3, auroraDouble.AvailableRooms);
        Assert.Equal(2 * 12900, auroraDouble.TotalPriceCents);
    }

    [Fact]
    public async Task Confirmed_bookings_and_active_holds_take_rooms_but_expired_holds_do_not()
    {
        await factory.AddBookingsAsync(
            factory.NewBooking(BookingStatus.Confirmed),
            factory.NewBooking(BookingStatus.Held, expiresIn: TimeSpan.FromMinutes(10)),
            // Status not yet updated by the expiry job, but ExpiresAt has passed.
            factory.NewBooking(BookingStatus.Held, expiresIn: TimeSpan.FromMinutes(-1)),
            factory.NewBooking(BookingStatus.Expired),
            factory.NewBooking(BookingStatus.Cancelled));

        var response = await Search("Helsinki", "2026-11-10", "2026-11-12");

        Assert.Equal(1, AuroraDoubleRoom(response).AvailableRooms);
    }

    [Fact]
    public async Task Back_to_back_bookings_take_only_one_room()
    {
        await factory.AddBookingsAsync(
            factory.NewBooking(BookingStatus.Confirmed, checkIn: "2026-11-10", checkOut: "2026-11-11"),
            factory.NewBooking(BookingStatus.Confirmed, checkIn: "2026-11-11", checkOut: "2026-11-12"));

        var response = await Search("Helsinki", "2026-11-10", "2026-11-12");

        Assert.Equal(2, AuroraDoubleRoom(response).AvailableRooms);
    }

    [Fact]
    public async Task Booking_ending_on_check_in_day_does_not_overlap()
    {
        await factory.AddBookingsAsync(
            factory.NewBooking(BookingStatus.Confirmed, checkIn: "2026-11-08", checkOut: "2026-11-10"));

        var response = await Search("Helsinki", "2026-11-10", "2026-11-12");

        Assert.Equal(3, AuroraDoubleRoom(response).AvailableRooms);
    }

    [Theory]
    [InlineData("2026-11-12", "2026-11-10")] // check-out before check-in
    [InlineData("2026-11-10", "2026-11-10")] // zero nights
    [InlineData("2026-10-31", "2026-11-02")] // check-in in the past
    [InlineData("2026-11-10", "2026-12-11")] // more than 30 nights
    [InlineData("10.11.2026", "12.11.2026")] // wrong format
    public async Task Invalid_dates_are_rejected(string checkIn, string checkOut)
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() => Search("Helsinki", checkIn, checkOut));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    private async Task<SearchAvailabilityResponse> Search(string city, string checkIn, string checkOut) =>
        await _client.SearchAvailabilityAsync(new SearchAvailabilityRequest
        {
            City = city,
            CheckInDate = checkIn,
            CheckOutDate = checkOut
        });

    private static RoomTypeAvailability AuroraDoubleRoom(SearchAvailabilityResponse response) =>
        response.RoomTypes.Single(rt => rt.RoomTypeId == AuroraDouble);
}
