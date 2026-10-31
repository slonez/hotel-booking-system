namespace HotelBooking.Messaging;

public static class Exchanges
{
    // One topic exchange for all integration events. The routing key is the event type.
    public const string Events = "hotel-booking";
}
