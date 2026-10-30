using BookingService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookingService.Tests;

[Collection(BookingServiceCollection.Name)]
public class HoldExpiryJobTests(BookingServiceFactory factory) : IAsyncLifetime
{
    // Resolving the job starts the service, which applies migrations and seed data.
    private readonly HoldExpiryJob _job = factory.Services.GetRequiredService<HoldExpiryJob>();

    public Task InitializeAsync() => factory.DeleteBookingsAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Expires_only_holds_past_their_expiry_time()
    {
        var expiredHold = factory.NewBooking(BookingStatus.Held, expiresIn: TimeSpan.FromMinutes(-1));
        var activeHold = factory.NewBooking(BookingStatus.Held, expiresIn: TimeSpan.FromMinutes(1));
        var confirmed = factory.NewBooking(BookingStatus.Confirmed, expiresIn: TimeSpan.FromMinutes(-1));
        await factory.AddBookingsAsync(expiredHold, activeHold, confirmed);

        var count = await _job.ExpireHoldsAsync(CancellationToken.None);

        await using var db = factory.CreateDbContext();
        var statuses = await db.Bookings.ToDictionaryAsync(b => b.Id, b => b.Status);
        Assert.Equal(1, count);
        Assert.Equal(BookingStatus.Expired, statuses[expiredHold.Id]);
        Assert.Equal(BookingStatus.Held, statuses[activeHold.Id]);
        Assert.Equal(BookingStatus.Confirmed, statuses[confirmed.Id]);
    }
}
