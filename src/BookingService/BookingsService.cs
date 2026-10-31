using System.Globalization;
using BookingService.Data;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using HotelBooking.Contracts.Booking;
using HotelBooking.Contracts.Events;
using HotelBooking.Contracts.Payment;
using HotelBooking.Messaging;
using Microsoft.EntityFrameworkCore;

namespace BookingService;

public class BookingsService(BookingDbContext db, Payments.PaymentsClient payments, TimeProvider time) : Bookings.BookingsBase
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

    public override async Task<ListBookingsResponse> ListBookings(
        ListBookingsRequest request, ServerCallContext context)
    {
        var now = time.GetUtcNow();
        var bookings = await db.Bookings
            .Where(b => b.ClerkUserId == request.ClerkUserId)
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => new
            {
                b.Id, b.Status, b.CheckInDate, b.CheckOutDate, b.TotalPriceCents, b.ExpiresAt, b.RefundAmountCents,
                HotelName = b.RoomType.Hotel.Name,
                RoomTypeName = b.RoomType.Name
            })
            .ToListAsync(context.CancellationToken);

        var response = new ListBookingsResponse();
        foreach (var b in bookings)
        {
            var summary = new BookingSummary
            {
                BookingId = b.Id.ToString(),
                HotelName = b.HotelName,
                RoomTypeName = b.RoomTypeName,
                CheckInDate = FormatDate(b.CheckInDate),
                CheckOutDate = FormatDate(b.CheckOutDate),
                // Same rule as availability: a hold past ExpiresAt is expired even before the job runs.
                Status = (b.Status == BookingStatus.Held && b.ExpiresAt <= now ? BookingStatus.Expired : b.Status).ToString(),
                TotalPriceCents = b.TotalPriceCents,
                ExpiresAt = Timestamp.FromDateTimeOffset(b.ExpiresAt)
            };
            // Proto optional fields cannot be set to null; leave unset instead.
            if (b.RefundAmountCents is { } refund)
                summary.RefundAmountCents = refund;
            response.Bookings.Add(summary);
        }
        return response;
    }

    public override async Task<CancelBookingResponse> CancelBooking(
        CancelBookingRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var now = time.GetUtcNow();

        var booking = await FindOwnBooking(request.BookingId, request.ClerkUserId, ct);
        if (booking.Status != BookingStatus.Confirmed)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Only confirmed bookings can be cancelled."));

        var checkIn = CancellationPolicy.CheckInMoment(booking.CheckInDate, booking.RoomType.Hotel);
        var refund = CancellationPolicy.RefundAmount(booking.TotalPriceCents, checkIn, now)
            ?? throw new RpcException(new Status(StatusCode.FailedPrecondition, "Bookings cannot be cancelled after check-in time."));

        // The status change and the refund event are saved together (transactional outbox).
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Atomic transition: only succeeds if the booking is still Confirmed.
        var updated = await db.Bookings
            .Where(b => b.Id == booking.Id && b.Status == BookingStatus.Confirmed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BookingStatus.Cancelled)
                .SetProperty(b => b.CancelledAt, now)
                .SetProperty(b => b.RefundAmountCents, refund), ct);
        if (updated == 0)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Only confirmed bookings can be cancelled."));

        db.OutboxMessages.Add(OutboxMessage.Create(RefundRequested.RoutingKey,
            RefundRequestedFor(booking, refund, RefundReason.Cancelled), now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new CancelBookingResponse { RefundAmountCents = refund };
    }

    public override async Task<StartPaymentResponse> StartPayment(
        StartPaymentRequest request, ServerCallContext context)
    {
        var booking = await FindOwnBooking(request.BookingId, request.ClerkUserId, context.CancellationToken);
        if (booking.Status != BookingStatus.Held || booking.ExpiresAt <= time.GetUtcNow())
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Only bookings on hold can be paid."));

        // Deadline and cancellation come from this request (call context propagation).
        // Errors from Payment Service, such as Unavailable, are passed on to the caller as they are.
        var response = await payments.CreateCheckoutSessionAsync(new CreateCheckoutSessionRequest
        {
            BookingId = booking.Id.ToString(),
            AmountCents = booking.TotalPriceCents,
            Description = $"{booking.RoomType.Hotel.Name}, {booking.RoomType.Name}, " +
                $"{FormatDate(booking.CheckInDate)} – {FormatDate(booking.CheckOutDate)}",
            CustomerEmail = booking.Email
        });

        return new StartPaymentResponse { CheckoutUrl = response.CheckoutUrl };
    }

    // Expects booking.RoomType.Hotel to be loaded.
    public static RefundRequested RefundRequestedFor(Booking booking, long amountCents, RefundReason reason) =>
        new(booking.Id, booking.Email, booking.RoomType.Hotel.Name, booking.RoomType.Name, amountCents, reason);

    // Another user's booking looks the same as a missing one, so its existence is not revealed (NFR-6).
    private async Task<Booking> FindOwnBooking(string bookingId, string clerkUserId, CancellationToken ct)
    {
        var booking = Guid.TryParse(bookingId, out var id)
            ? await db.Bookings
                .Include(b => b.RoomType).ThenInclude(rt => rt.Hotel)
                .SingleOrDefaultAsync(b => b.Id == id && b.ClerkUserId == clerkUserId, ct)
            : null;
        return booking ?? throw new RpcException(new Status(StatusCode.NotFound, "Booking not found."));
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

    private static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static RpcException InvalidArgument(string message) =>
        new(new Status(StatusCode.InvalidArgument, message));
}
