using Microsoft.EntityFrameworkCore;

namespace PaymentService.Data;

public class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var payment = modelBuilder.Entity<Payment>();
        payment.Property(p => p.Status).HasConversion<string>();
        // One payment per booking (NFR-2).
        payment.HasIndex(p => p.BookingId).IsUnique();
        payment.HasIndex(p => p.StripeSessionId).IsUnique();
    }
}
