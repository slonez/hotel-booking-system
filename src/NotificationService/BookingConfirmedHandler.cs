using System.Globalization;
using HotelBooking.Contracts.Events;
using HotelBooking.Messaging;
using NotificationService.Data;

namespace NotificationService;

public class BookingConfirmedHandler(NotificationSender sender) : IEventHandler<BookingConfirmed>
{
    public const string Queue = "notification.booking-confirmed";

    public Task HandleAsync(BookingConfirmed message, CancellationToken ct) =>
        sender.SendOnceAsync(message.BookingId, NotificationType.BookingConfirmed, message.Email,
            $"Your booking at {message.HotelName} is confirmed",
            $"""
            Your booking is confirmed.

            Hotel: {message.HotelName}
            Room: {message.RoomTypeName}
            Check-in: {message.CheckInDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}
            Check-out: {message.CheckOutDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}
            Total: {Money.Format(message.TotalPriceCents)}
            """,
            ct);
}
