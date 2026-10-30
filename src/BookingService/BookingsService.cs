using System.Globalization;
using BookingService.Data;
using Grpc.Core;
using HotelBooking.Contracts.Booking;
using Microsoft.EntityFrameworkCore;

namespace BookingService;

public class BookingsService(BookingDbContext db, TimeProvider time) : Bookings.BookingsBase
{
    private const int MaxNights = 30;

    public override async Task<SearchAvailabilityResponse> SearchAvailability(
        SearchAvailabilityRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.City))
            throw InvalidArgument("City is required.");

        var (checkIn, checkOut) = ParseStay(request.CheckInDate, request.CheckOutDate);
        var nights = checkOut.DayNumber - checkIn.DayNumber;

        var roomTypes = await db.RoomTypes
            .Where(rt => EF.Functions.ILike(rt.Hotel.City, request.City))
            .OrderBy(rt => rt.Hotel.Name).ThenBy(rt => rt.Name)
            .Select(rt => new { rt.Id, rt.Name, rt.PricePerNightCents, rt.TotalRooms, rt.HotelId, HotelName = rt.Hotel.Name })
            .ToListAsync(context.CancellationToken);

        var roomTypeIds = roomTypes.Select(rt => rt.Id).ToList();
        var bookings = await db.Bookings
            .Where(b => roomTypeIds.Contains(b.RoomTypeId) && b.CheckInDate < checkOut && b.CheckOutDate > checkIn)
            .Where(Availability.TakesRoom(time.GetUtcNow()))
            .ToListAsync(context.CancellationToken);

        var response = new SearchAvailabilityResponse();
        foreach (var rt in roomTypes)
        {
            var taken = Availability.MaxRoomsTaken(bookings.Where(b => b.RoomTypeId == rt.Id), checkIn, checkOut);
            response.RoomTypes.Add(new RoomTypeAvailability
            {
                HotelId = rt.HotelId,
                HotelName = rt.HotelName,
                RoomTypeId = rt.Id,
                RoomTypeName = rt.Name,
                PricePerNightCents = rt.PricePerNightCents,
                TotalPriceCents = rt.PricePerNightCents * nights,
                AvailableRooms = rt.TotalRooms - taken
            });
        }
        return response;
    }

    private (DateOnly CheckIn, DateOnly CheckOut) ParseStay(string checkInText, string checkOutText)
    {
        if (!TryParseDate(checkInText, out var checkIn) || !TryParseDate(checkOutText, out var checkOut))
            throw InvalidArgument("Dates must be in yyyy-MM-dd format.");
        if (checkOut <= checkIn)
            throw InvalidArgument("Check-out must be after check-in.");
        if (checkOut.DayNumber - checkIn.DayNumber > MaxNights)
            throw InvalidArgument($"Maximum stay is {MaxNights} nights.");
        if (checkIn < DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime))
            throw InvalidArgument("Check-in cannot be in the past.");

        return (checkIn, checkOut);
    }

    private static bool TryParseDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static RpcException InvalidArgument(string message) =>
        new(new Status(StatusCode.InvalidArgument, message));
}
