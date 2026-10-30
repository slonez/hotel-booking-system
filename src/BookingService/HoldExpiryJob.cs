using BookingService.Data;
using Microsoft.EntityFrameworkCore;

namespace BookingService;

// Marks holds past their ExpiresAt as Expired. Availability already ignores expired holds
// (see Availability.TakesRoom); this job only keeps the stored status up to date.
public class HoldExpiryJob(IServiceScopeFactory scopes, TimeProvider time, ILogger<HoldExpiryJob> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await ExpireHoldsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An exception here would stop the whole service. Log and retry on the next tick.
                logger.LogError(ex, "Failed to expire holds");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> ExpireHoldsAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
        var now = time.GetUtcNow();

        // Single atomic UPDATE. The Status condition means a booking confirmed at the
        // same moment is not touched.
        var expired = await db.Bookings
            .Where(b => b.Status == BookingStatus.Held && b.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Expired), ct);

        if (expired > 0)
            logger.LogInformation("Expired {Count} holds", expired);

        return expired;
    }
}
