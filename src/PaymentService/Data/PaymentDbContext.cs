using Microsoft.EntityFrameworkCore;

namespace PaymentService.Data;

public class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var payment = modelBuilder.Entity<Payment>();
        payment.Property(p => p.Status).HasConversion<string>();
        // One payment per booking (NFR-2).
        payment.HasIndex(p => p.BookingId).IsUnique();
        payment.HasIndex(p => p.StripeSessionId).IsUnique();

        modelBuilder.Entity<OutboxMessage>()
            .Property(m => m.Payload)
            .HasColumnType("jsonb");
        // The publisher only reads unpublished messages.
        modelBuilder.Entity<OutboxMessage>()
            .HasIndex(m => m.CreatedAt)
            .HasFilter("\"PublishedAt\" IS NULL");
    }
}
