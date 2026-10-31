namespace PaymentService.Data;

public class Payment
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public required string StripeSessionId { get; set; }
    public required string CheckoutUrl { get; set; }
    // Set when the payment succeeds; refunds are made against it.
    public string? StripePaymentIntentId { get; set; }
    public long AmountCents { get; set; }
    public PaymentStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum PaymentStatus
{
    Pending,
    Succeeded,
    Expired
}
