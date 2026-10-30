namespace BookingService.Data;

public class RoomType
{
    public int Id { get; set; }
    public int HotelId { get; set; }
    public Hotel Hotel { get; set; } = null!;
    public required string Name { get; set; }
    public long PricePerNightCents { get; set; }
    public int TotalRooms { get; set; }
}
