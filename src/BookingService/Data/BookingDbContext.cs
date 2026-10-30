using Microsoft.EntityFrameworkCore;

namespace BookingService.Data;

public class BookingDbContext(DbContextOptions<BookingDbContext> options) : DbContext(options)
{
    public DbSet<Hotel> Hotels => Set<Hotel>();
    public DbSet<RoomType> RoomTypes => Set<RoomType>();
    public DbSet<Booking> Bookings => Set<Booking>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Booking>()
            .Property(b => b.Status)
            .HasConversion<string>();

        modelBuilder.Entity<Hotel>().HasData(
            new Hotel { Id = 1, Name = "Hotel Aurora", Address = "Mannerheimintie 10", City = "Helsinki", ZipCode = "00100", Country = "Finland", CheckInTime = new TimeOnly(15, 0), TimeZone = "Europe/Helsinki" },
            new Hotel { Id = 2, Name = "Seaside Hotel", Address = "Ruoholahdenranta 3", City = "Helsinki", ZipCode = "00180", Country = "Finland", CheckInTime = new TimeOnly(14, 0), TimeZone = "Europe/Helsinki" },
            new Hotel { Id = 3, Name = "Lakeview Hotel", Address = "Hämeenkatu 5", City = "Tampere", ZipCode = "33100", Country = "Finland", CheckInTime = new TimeOnly(15, 0), TimeZone = "Europe/Helsinki" });

        modelBuilder.Entity<RoomType>().HasData(
            new RoomType { Id = 1, HotelId = 1, Name = "Single Room", PricePerNightCents = 8900, TotalRooms = 5 },
            new RoomType { Id = 2, HotelId = 1, Name = "Double Room", PricePerNightCents = 12900, TotalRooms = 3 },
            new RoomType { Id = 3, HotelId = 2, Name = "Double Room", PricePerNightCents = 14900, TotalRooms = 2 },
            new RoomType { Id = 4, HotelId = 2, Name = "Suite", PricePerNightCents = 29900, TotalRooms = 1 },
            new RoomType { Id = 5, HotelId = 3, Name = "Double Room", PricePerNightCents = 10900, TotalRooms = 4 });
    }
}
