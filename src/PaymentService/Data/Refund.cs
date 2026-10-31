using HotelBooking.Contracts.Events;

namespace PaymentService.Data;

public class Refund
{
    // Also the Stripe idempotency key, so retries cannot refund twice.
    public Guid Id { get; set; }
    public Guid PaymentId { get; set; }
    public Payment Payment { get; set; } = null!;
    public long AmountCents { get; set; }
    public RefundReason Reason { get; set; }
    public RefundStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? StripeRefundId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum RefundStatus
{
    Pending,
    Succeeded,
    // Still failing after several attempts; handled manually.
    Failed
}
