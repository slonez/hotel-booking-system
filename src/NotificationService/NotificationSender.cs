using Microsoft.EntityFrameworkCore;
using NotificationService.Data;

namespace NotificationService;

// Sends each kind of email once per booking. The email goes first and the log row after it: SMTP and
// the database cannot share a transaction, and after a crash in between a duplicate email is better than a lost one.
public class NotificationSender(
    NotificationDbContext db,
    EmailSender email,
    TimeProvider time,
    ILogger<NotificationSender> logger)
{
    public async Task SendOnceAsync(
        Guid bookingId, NotificationType type, string to, string subject, string body, CancellationToken ct)
    {
        if (await db.Notifications.AnyAsync(n => n.BookingId == bookingId && n.Type == type, ct))
        {
            logger.LogInformation("{Type} for booking {BookingId} is already sent; ignoring duplicate", type, bookingId);
            return;
        }

        await email.SendAsync(to, subject, body, ct);

        db.Notifications.Add(new Notification
        {
            Id = Guid.CreateVersion7(),
            BookingId = bookingId,
            Type = type,
            SentAt = time.GetUtcNow()
        });
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Sent {Type} for booking {BookingId}", type, bookingId);
    }
}
