namespace BookingService.Data;

public class Hotel
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Address { get; set; }
    public required string City { get; set; }
    public required string ZipCode { get; set; }
    public required string Country { get; set; }
    public TimeOnly CheckInTime { get; set; }

    // IANA time zone, e.g. "Europe/Helsinki".
    public required string TimeZone { get; set; }

    public List<RoomType> RoomTypes { get; set; } = [];
}
