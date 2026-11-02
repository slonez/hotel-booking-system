namespace NotificationService.Data;

// A sent email. Stops duplicate messages from sending it again. Holds no customer data:
// everything needed for an email arrives in the event.
public class Notification
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public NotificationType Type { get; set; }
    public DateTimeOffset SentAt { get; set; }
}

public enum NotificationType
{
    BookingConfirmed,
    BookingCancelled,
    LatePaymentRefund
}
