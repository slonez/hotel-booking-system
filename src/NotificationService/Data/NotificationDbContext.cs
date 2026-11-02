using Microsoft.EntityFrameworkCore;

namespace NotificationService.Data;

public class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var notification = modelBuilder.Entity<Notification>();
        notification.Property(n => n.Type).HasConversion<string>();
        // Each kind of email is sent once per booking.
        notification.HasIndex(n => new { n.BookingId, n.Type }).IsUnique();
    }
}
