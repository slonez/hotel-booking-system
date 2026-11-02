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

    // Resolving the services starts the service, which applies migrations and seed data.
    private readonly IServiceProvider _services = factory.Services;

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
        await using var db = factory.CreateDbContext();
        Assert.Equal(late.TotalPriceCents, await db.Bookings.Where(b => b.Id == late.Id).Select(b => b.RefundAmountCents).SingleAsync());
    }

    [Fact]
    public async Task Duplicate_message_does_not_confirm_a_refunded_booking_after_a_room_becomes_free()
    {
        var late = factory.NewBooking(BookingStatus.Expired, roomTypeId: SeasideSuite, userId: "user_late", expiresIn: TimeSpan.FromMinutes(-1));
        var other = factory.NewBooking(BookingStatus.Confirmed, roomTypeId: SeasideSuite, userId: "user_other");
        await factory.AddBookingsAsync(late, other);
        await Handle(late);
        await using (var db = factory.CreateDbContext())
            await db.Bookings.Where(b => b.Id == other.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Cancelled));

        await Handle(late);

        Assert.Equal(BookingStatus.Expired, await GetStatus(late));
        Assert.Equal(1, await factory.CountOutboxMessagesAsync());
    }

    [Fact]
    public async Task Hold_that_expires_while_waiting_for_the_lock_does_not_overbook()
    {
        var hold = factory.NewBooking(BookingStatus.Held, roomTypeId: SeasideSuite, userId: "user_hold", expiresIn: TimeSpan.FromMinutes(1));
        await factory.AddBookingsAsync(hold);

        // Plays the part of CreateBooking: holds the room type lock while the hold expires, then takes the room.
        await using var db = factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.RoomTypes.FromSql($"""SELECT * FROM "RoomTypes" WHERE "Id" = {SeasideSuite} FOR UPDATE""").SingleAsync();

        var handling = Handle(hold);
        await WaitUntilAQueryIsWaitingForALock(db);
        factory.Time.Advance(TimeSpan.FromMinutes(2));
        db.Bookings.Add(factory.NewBooking(BookingStatus.Confirmed, roomTypeId: SeasideSuite, userId: "user_new"));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await handling;

        Assert.Equal(BookingStatus.Expired, await GetStatus(hold));
        Assert.Equal(1, await factory.CountOutboxMessagesAsync());
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

    private static async Task WaitUntilAQueryIsWaitingForALock(BookingDbContext db)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await db.Database
            .SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM pg_stat_activity WHERE wait_event_type = 'Lock'""")
            .SingleAsync() == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "No query started waiting for the lock within 10 seconds.");
            await Task.Delay(50);
        }
    }

    private async Task Handle(Booking booking)
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PaymentSucceededHandler>()
            .HandleAsync(new PaymentSucceeded(booking.Id, Guid.CreateVersion7(), booking.TotalPriceCents), CancellationToken.None);
    }

    private async Task<BookingStatus> GetStatus(Booking booking)
    {
        await using var db = factory.CreateDbContext();
        return await db.Bookings.Where(b => b.Id == booking.Id).Select(b => b.Status).SingleAsync();
    }
}
