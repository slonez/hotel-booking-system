using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace HotelBooking.Messaging;

// An event saved in the same transaction as the state change, published later by OutboxPublisher.
public class OutboxMessage
{
    // Also the RabbitMQ message ID.
    public Guid Id { get; set; }
    // RabbitMQ routing key, e.g. "payment.succeeded".
    public required string Type { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    public static OutboxMessage Create<T>(string type, T payload, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        Type = type,
        Payload = JsonSerializer.Serialize(payload),
        CreatedAt = now
    };
}

public static class OutboxModelBuilderExtensions
{
    // Call from OnModelCreating in a DbContext that has a DbSet<OutboxMessage>.
    public static void ConfigureOutbox(this ModelBuilder modelBuilder)
    {
        var message = modelBuilder.Entity<OutboxMessage>();
        message.Property(m => m.Payload).HasColumnType("jsonb");
        // The publisher only reads unpublished messages.
        message.HasIndex(m => m.CreatedAt).HasFilter("\"PublishedAt\" IS NULL");
    }
}
