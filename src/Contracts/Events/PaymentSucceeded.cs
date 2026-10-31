namespace HotelBooking.Contracts.Events;

// Published by Payment Service when Stripe confirms a payment. Consumed by Booking Service.
public record PaymentSucceeded(Guid BookingId, Guid PaymentId, long AmountCents)
{
    public const string RoutingKey = "payment.succeeded";
}
