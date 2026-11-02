namespace HotelBooking.Contracts.Events;

// Published by Booking Service when a booking is confirmed. Consumed by Notification Service,
// which gets everything it needs for the email from this event.
public record BookingConfirmed(
    Guid BookingId,
    string Email,
    string HotelName,
    string RoomTypeName,
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    long TotalPriceCents)
{
    public const string RoutingKey = "booking.confirmed";
}
