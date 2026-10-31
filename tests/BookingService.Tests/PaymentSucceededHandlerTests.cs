using BookingService.Data;
using HotelBooking.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookingService.Tests;

// Seed data: Seaside "Suite" (id 4) has 1 room. Test time is 2026-11-01 12:00 UTC.
[Collection(BookingServiceCollection.Name)]
public class PaymentSucceededHandlerTests(BookingServiceFactory factory) : IAsyncLifetime
{
    private const int SeasideSuite = 4;

    public Task InitializeAsync() => factory.DeleteBookingsAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(BookingStatus.Held, 10)]
    // Late payments while the room is still free.
    [InlineData(BookingStatus.Held, -1)]
    [InlineData(BookingStatus.Expired, -1)]
    public async Task Confirms_booking(BookingStatus status, int expiresInMinutes)
    {
        var booking = factory.NewBooking(status, roomTypeId: SeasideSuite, expiresIn: TimeSpan.FromMinutes(expiresInMinutes));
        await factory.AddBookingsAsync(booking);

        await Handle(booking);

        Assert.Equal(BookingStatus.Confirmed, await GetStatus(booking));
        Assert.Equal(0, await factory.CountOutboxMessagesAsync());
    }

    [Fact]
    public async Task Late_payment_without_room_keeps_booking_expired_and_requests_full_refund()
    {
        var late = factory.NewBooking(BookingStatus.Expired, roomTypeId: SeasideSuite, userId: "user_late", expiresIn: TimeSpan.FromMinutes(-1));
        var other = factory.NewBooking(BookingStatus.Confirmed, roomTypeId: SeasideSuite, userId: "user_other",
            checkIn: "2026-11-11", checkOut: "2026-11-13");
        await factory.AddBookingsAsync(late, other);

        await Handle(late);

        Assert.Equal(BookingStatus.Expired, await GetStatus(late));
        Assert.Equal(new RefundRequested(late.Id, "user_late@example.com", "Seaside Hotel", "Suite", late.TotalPriceCents, RefundReason.LatePayment),
            await factory.SingleOutboxMessageAsync<RefundRequested>(RefundRequested.RoutingKey));
    }

    [Theory]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.Cancelled)]
    public async Task Ignores_duplicate_or_outdated_message(BookingStatus status)
    {
        var booking = factory.NewBooking(status, roomTypeId: SeasideSuite);
        await factory.AddBookingsAsync(booking);

        await Handle(booking);

        Assert.Equal(status, await GetStatus(booking));
    }

    [Fact]
    public async Task Ignores_unknown_booking()
    {
        await Handle(factory.NewBooking(BookingStatus.Held));
    }

    private async Task Handle(Booking booking)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PaymentSucceededHandler>()
            .HandleAsync(new PaymentSucceeded(booking.Id, Guid.CreateVersion7(), booking.TotalPriceCents), CancellationToken.None);
    }

    private async Task<BookingStatus> GetStatus(Booking booking)
    {
        await using var db = factory.CreateDbContext();
        return await db.Bookings.Where(b => b.Id == booking.Id).Select(b => b.Status).SingleAsync();
    }
}
