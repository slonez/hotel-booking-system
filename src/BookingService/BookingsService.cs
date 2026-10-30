using System.Globalization;
using BookingService.Data;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using HotelBooking.Contracts.Booking;
using Microsoft.EntityFrameworkCore;

namespace BookingService;

public class BookingsService(BookingDbContext db, TimeProvider time) : Bookings.BookingsBase
{
    private const int MaxNights = 30;
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(30);

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

    public override async Task<CreateBookingResponse> CreateBooking(
        CreateBookingRequest request, ServerCallContext context)
    {
        var (checkIn, checkOut) = ParseStay(request.CheckInDate, request.CheckOutDate);
        var ct = context.CancellationToken;
        var now = time.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Locks the room type row until commit. Concurrent bookings for the same
        // room type wait here, so availability is checked one booking at a time.
        var roomType = await db.RoomTypes
            .FromSql($"""SELECT * FROM "RoomTypes" WHERE "Id" = {request.RoomTypeId} FOR UPDATE""")
            .SingleOrDefaultAsync(ct)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Room type not found."));

        var bookings = await db.Bookings
            .Where(b => b.RoomTypeId == roomType.Id && b.CheckInDate < checkOut && b.CheckOutDate > checkIn)
            .Where(Availability.TakesRoom(now))
            .ToListAsync(ct);

        if (Availability.MaxRoomsTaken(bookings, checkIn, checkOut) >= roomType.TotalRooms)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "No rooms available for the selected dates."));

        var booking = new Booking
        {
            Id = Guid.CreateVersion7(),
            RoomTypeId = roomType.Id,
            ClerkUserId = request.ClerkUserId,
            Email = request.Email,
            Status = BookingStatus.Held,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            TotalPriceCents = roomType.PricePerNightCents * (checkOut.DayNumber - checkIn.DayNumber),
            ExpiresAt = now + HoldDuration,
            CreatedAt = now
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new CreateBookingResponse
        {
            BookingId = booking.Id.ToString(),
            TotalPriceCents = booking.TotalPriceCents,
            ExpiresAt = Timestamp.FromDateTimeOffset(booking.ExpiresAt)
        };
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
