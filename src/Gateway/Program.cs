using System.Security.Claims;
using System.Threading.RateLimiting;
using Gateway;
using HotelBooking.Contracts.Booking;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpcClient<Bookings.BookingsClient>(options =>
    options.Address = new Uri(builder.Configuration["Services:Booking"]!))
    .AddInterceptor(_ => new DeadlineInterceptor());
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GrpcExceptionHandler>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Clerk publishes its signing keys under the Frontend API URL.
        options.Authority = builder.Configuration["Clerk:Authority"];
        // Clerk session tokens have no audience claim.
        options.TokenValidationParameters.ValidateAudience = false;
        // Keep claim names as in the token ("sub", "email").
        options.MapInboundClaims = false;
    });
builder.Services.AddAuthorization();

// Turn on only when a reverse proxy is the only way to reach the Gateway: the header is then trusted
// from any address. Otherwise clients could fake their IP and get around the rate limit.
var useForwardedHeaders = builder.Configuration.GetValue<bool>("ForwardedHeaders:Enabled");
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Per client IP.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("RateLimit:RequestsPerMinute", 100),
                Window = TimeSpan.FromMinutes(1)
            }));
});

var app = builder.Build();

app.UseExceptionHandler();
if (useForwardedHeaders)
    app.UseForwardedHeaders();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

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

app.MapPost("/api/bookings", async (CreateBookingBody body, ClaimsPrincipal user,
    Bookings.BookingsClient bookings, CancellationToken ct) =>
{
    var response = await bookings.CreateBookingAsync(new CreateBookingRequest
    {
        RoomTypeId = body.RoomTypeId,
        CheckInDate = body.CheckIn,
        CheckOutDate = body.CheckOut,
        ClerkUserId = user.FindFirstValue("sub")!,
        Email = user.FindFirstValue("email")!
    }, cancellationToken: ct);

    return TypedResults.Created($"/api/bookings/{response.BookingId}", new BookingCreated(
        response.BookingId, response.TotalPriceCents, response.ExpiresAt.ToDateTimeOffset()));
})
// The email claim is added to the Clerk session token in the Clerk dashboard.
.RequireAuthorization(policy => policy.RequireClaim("email"));

// The user ID comes from the token, so users can only list their own bookings (NFR-6).
app.MapGet("/api/bookings", async (ClaimsPrincipal user, Bookings.BookingsClient bookings, CancellationToken ct) =>
{
    var response = await bookings.ListBookingsAsync(
        new ListBookingsRequest { ClerkUserId = user.FindFirstValue("sub")! }, cancellationToken: ct);

    return response.Bookings.Select(b => new BookingResult(
        b.BookingId, b.HotelName, b.RoomTypeName, b.CheckInDate, b.CheckOutDate,
        b.Status, b.TotalPriceCents, b.ExpiresAt.ToDateTimeOffset(),
        b.HasRefundAmountCents ? b.RefundAmountCents : null));
})
.RequireAuthorization();

// Cancelling is an action, not a delete: the booking stays, its status changes.
app.MapPost("/api/bookings/{bookingId}/cancel", async (string bookingId, ClaimsPrincipal user,
    Bookings.BookingsClient bookings, CancellationToken ct) =>
{
    var response = await bookings.CancelBookingAsync(new CancelBookingRequest
    {
        BookingId = bookingId,
        ClerkUserId = user.FindFirstValue("sub")!
    }, cancellationToken: ct);

    return new BookingCancelled(response.RefundAmountCents);
})
.RequireAuthorization();

// Creating a Stripe session can be slow, so this call gets a longer deadline than the default.
// Booking passes the deadline on to Payment Service.
app.MapPost("/api/bookings/{bookingId}/checkout", async (string bookingId, ClaimsPrincipal user,
    Bookings.BookingsClient bookings, CancellationToken ct) =>
{
    var response = await bookings.StartPaymentAsync(new StartPaymentRequest
    {
        BookingId = bookingId,
        ClerkUserId = user.FindFirstValue("sub")!
    }, deadline: DateTime.UtcNow.AddSeconds(15), cancellationToken: ct);

    return new CheckoutStarted(response.CheckoutUrl);
})
.RequireAuthorization();

app.Run();

record RoomTypeResult(
    int HotelId,
    string HotelName,
    int RoomTypeId,
    string RoomTypeName,
    long PricePerNightCents,
    long TotalPriceCents,
    int AvailableRooms);

record CreateBookingBody(int RoomTypeId, string CheckIn, string CheckOut);

record BookingCreated(string BookingId, long TotalPriceCents, DateTimeOffset ExpiresAt);

record BookingResult(
    string BookingId,
    string HotelName,
    string RoomTypeName,
    string CheckIn,
    string CheckOut,
    string Status,
    long TotalPriceCents,
    DateTimeOffset ExpiresAt,
    long? RefundAmountCents);

record BookingCancelled(long RefundAmountCents);

record CheckoutStarted(string CheckoutUrl);
