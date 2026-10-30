namespace BookingService.Data;

public class Booking
{
    public Guid Id { get; set; }
    public int RoomTypeId { get; set; }
    public RoomType RoomType { get; set; } = null!;
    public required string ClerkUserId { get; set; }
    public required string Email { get; set; }
    public BookingStatus Status { get; set; }
    public DateOnly CheckInDate { get; set; }
    public DateOnly CheckOutDate { get; set; }
    public long TotalPriceCents { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public long? RefundAmountCents { get; set; }
}

public enum BookingStatus
{
    Held,
    Confirmed,
    Expired,
    Cancelled
}
