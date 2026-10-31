using HotelBooking.Messaging;
using Microsoft.EntityFrameworkCore;

namespace PaymentService.Data;

public class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var payment = modelBuilder.Entity<Payment>();
        payment.Property(p => p.Status).HasConversion<string>();
        // One payment per booking (NFR-2).
        payment.HasIndex(p => p.BookingId).IsUnique();
        payment.HasIndex(p => p.StripeSessionId).IsUnique();

        var refund = modelBuilder.Entity<Refund>();
        refund.Property(r => r.Reason).HasConversion<string>();
        refund.Property(r => r.Status).HasConversion<string>();
        // One refund per payment: a duplicate RefundRequested cannot refund twice.
        refund.HasIndex(r => r.PaymentId).IsUnique();

        modelBuilder.ConfigureOutbox();
    }
}
