using BookingService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookingService.Tests;

public class HoldExpiryJobTests(BookingServiceFactory factory) : IClassFixture<BookingServiceFactory>, IAsyncLifetime
{
    // Resolving the job starts the service, which applies migrations and seed data.
    private readonly HoldExpiryJob _job = factory.Services.GetRequiredService<HoldExpiryJob>();

    public async Task InitializeAsync()
    {
        await using var db = factory.CreateDbContext();
        await db.Bookings.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Expires_only_holds_past_their_expiry_time()
    {
        var expiredHold = Booking(BookingStatus.Held, expiresIn: TimeSpan.FromMinutes(-1));
        var activeHold = Booking(BookingStatus.Held, expiresIn: TimeSpan.FromMinutes(1));
        var confirmed = Booking(BookingStatus.Confirmed, expiresIn: TimeSpan.FromMinutes(-1));
        await using (var db = factory.CreateDbContext())
        {
            db.Bookings.AddRange(expiredHold, activeHold, confirmed);
            await db.SaveChangesAsync();
        }

        var count = await _job.ExpireHoldsAsync(CancellationToken.None);

        await using var check = factory.CreateDbContext();
        var statuses = await check.Bookings.ToDictionaryAsync(b => b.Id, b => b.Status);
        Assert.Equal(1, count);
        Assert.Equal(BookingStatus.Expired, statuses[expiredHold.Id]);
        Assert.Equal(BookingStatus.Held, statuses[activeHold.Id]);
        Assert.Equal(BookingStatus.Confirmed, statuses[confirmed.Id]);
    }

    private Booking Booking(BookingStatus status, TimeSpan expiresIn) => new()
    {
        Id = Guid.CreateVersion7(),
        RoomTypeId = 2,
        ClerkUserId = "user_test",
        Email = "test@example.com",
        Status = status,
        CheckInDate = new DateOnly(2026, 11, 10),
        CheckOutDate = new DateOnly(2026, 11, 12),
        TotalPriceCents = 25800,
        ExpiresAt = factory.Time.GetUtcNow() + expiresIn,
        CreatedAt = factory.Time.GetUtcNow()
    };
}
