using System.Text.Json.Serialization;

namespace HotelBooking.Contracts.Events;

// Published by Booking Service when money must be returned. Payment Service executes the refund;
// Notification Service (Phase 6) uses the display fields for the email.
public record RefundRequested(
    Guid BookingId,
    string Email,
    string HotelName,
    string RoomTypeName,
    long AmountCents,
    RefundReason Reason)
{
    public const string RoutingKey = "refund.requested";
}

[JsonConverter(typeof(JsonStringEnumConverter<RefundReason>))]
public enum RefundReason
{
    Cancelled,
    // Paid after the hold expired and no room was left (FR-12).
    LatePayment
}
