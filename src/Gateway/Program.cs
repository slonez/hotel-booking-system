using Gateway;
using HotelBooking.Contracts.Booking;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpcClient<Bookings.BookingsClient>(options =>
    options.Address = new Uri(builder.Configuration["Services:Booking"]!));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GrpcExceptionHandler>();

var app = builder.Build();

app.UseExceptionHandler();

app.MapGet("/api/search", async (string city, string checkIn, string checkOut,
    Bookings.BookingsClient bookings, CancellationToken ct) =>
{
    var response = await bookings.SearchAvailabilityAsync(new SearchAvailabilityRequest
    {
        City = city,
        CheckInDate = checkIn,
        CheckOutDate = checkOut
    }, cancellationToken: ct);

    return response.RoomTypes.Select(rt => new RoomTypeResult(
        rt.HotelId, rt.HotelName, rt.RoomTypeId, rt.RoomTypeName,
        rt.PricePerNightCents, rt.TotalPriceCents, rt.AvailableRooms));
});

app.Run();

record RoomTypeResult(
    int HotelId,
    string HotelName,
    int RoomTypeId,
    string RoomTypeName,
    long PricePerNightCents,
    long TotalPriceCents,
    int AvailableRooms);
