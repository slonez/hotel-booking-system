using HotelBooking.Contracts.Events;
using HotelBooking.Messaging;
using NotificationService.Data;

namespace NotificationService;

// The same event covers a cancellation and a late payment; the reason decides the email.
public class RefundRequestedHandler(NotificationSender sender) : IEventHandler<RefundRequested>
{
    public const string Queue = "notification.refund-requested";

    public Task HandleAsync(RefundRequested message, CancellationToken ct)
    {
        var place = $"{message.HotelName}, {message.RoomTypeName}";
        var amount = Money.Format(message.AmountCents);

        var (type, subject, body) = message.Reason switch
        {
            RefundReason.Cancelled => (
                NotificationType.BookingCancelled,
                $"Your booking at {message.HotelName} is cancelled",
                $"Your booking at {place} is cancelled. We are refunding {amount} to your original payment method."),
            RefundReason.LatePayment => (
                NotificationType.LatePaymentRefund,
                $"We could not complete your booking at {message.HotelName}",
                $"Your payment for {place} arrived after your hold had expired and no room is left. " +
                $"We are refunding the full {amount} to your original payment method."),
            _ => throw new ArgumentOutOfRangeException(nameof(message), message.Reason, "Unknown refund reason.")
        };

        return sender.SendOnceAsync(message.BookingId, type, message.Email, subject, body, ct);
    }
}
