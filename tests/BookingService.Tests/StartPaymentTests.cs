using BookingService.Data;
using Grpc.Core;
using HotelBooking.Contracts.Booking;

namespace BookingService.Tests;

// Test time is 2026-11-01 12:00 UTC.
[Collection(BookingServiceCollection.Name)]
public class StartPaymentTests(BookingServiceFactory factory) : IAsyncLifetime
{
    // Creating the client starts the service, which applies migrations and seed data.
    private readonly Bookings.BookingsClient _client = factory.CreateGrpcClient();

    public Task InitializeAsync()
    {
        factory.Payments.Reset();
        return factory.DeleteBookingsAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Returns_checkout_url_for_own_booking_on_hold()
    {
        var booking = factory.NewBooking(BookingStatus.Held, userId: "user_a");
        await factory.AddBookingsAsync(booking);

        var response = await StartPayment(booking.Id, "user_a");

        Assert.Equal(FakePaymentsClient.CheckoutUrl, response.CheckoutUrl);
        var request = factory.Payments.LastRequest!;
        Assert.Equal((booking.Id.ToString(), 25800L, "user_a@example.com"),
            (request.BookingId, request.AmountCents, request.CustomerEmail));
        Assert.Equal("Hotel Aurora, Double Room, 2026-11-10 – 2026-11-12", request.Description);
    }

    [Fact]
    public async Task Another_users_booking_is_not_found()
    {
        var booking = factory.NewBooking(BookingStatus.Held, userId: "user_a");
        await factory.AddBookingsAsync(booking);

        var ex = await Assert.ThrowsAsync<RpcException>(() => StartPayment(booking.Id, "user_b"));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Theory]
    [InlineData(BookingStatus.Confirmed, 10)]
    [InlineData(BookingStatus.Cancelled, 10)]
    [InlineData(BookingStatus.Expired, -1)]
    // Hold past its expiry time, before the expiry job has run.
    [InlineData(BookingStatus.Held, -1)]
    public async Task Only_bookings_on_hold_can_be_paid(BookingStatus status, int expiresInMinutes)
    {
        var booking = factory.NewBooking(status, userId: "user_a", expiresIn: TimeSpan.FromMinutes(expiresInMinutes));
        await factory.AddBookingsAsync(booking);

        var ex = await Assert.ThrowsAsync<RpcException>(() => StartPayment(booking.Id, "user_a"));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Null(factory.Payments.LastRequest);
    }

    [Fact]
    public async Task Payment_service_error_is_passed_on()
    {
        var booking = factory.NewBooking(BookingStatus.Held, userId: "user_a");
        await factory.AddBookingsAsync(booking);
        factory.Payments.Error = new RpcException(new Status(StatusCode.Unavailable, "Payment provider is unavailable."));

        var ex = await Assert.ThrowsAsync<RpcException>(() => StartPayment(booking.Id, "user_a"));

        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
    }

    private async Task<StartPaymentResponse> StartPayment(Guid bookingId, string userId) =>
        await _client.StartPaymentAsync(new StartPaymentRequest
        {
            BookingId = bookingId.ToString(),
            ClerkUserId = userId
        });
}
