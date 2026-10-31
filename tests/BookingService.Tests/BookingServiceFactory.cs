using System.Globalization;
using System.Text.Json;
using BookingService.Data;
using Grpc.Net.Client;
using HotelBooking.Contracts.Booking;
using HotelBooking.Contracts.Payment;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace BookingService.Tests;

// All integration test classes share one service and one PostgreSQL container.
// Classes in the same collection run one after another, so they don't see each other's data.
[CollectionDefinition(Name)]
public class BookingServiceCollection : ICollectionFixture<BookingServiceFactory>
{
    public const string Name = "BookingService";
}

// Runs Booking Service in memory against real PostgreSQL and RabbitMQ in Docker.
public class BookingServiceFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public string RabbitMqConnectionString => _rabbitMq.GetConnectionString();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    public FakePaymentsClient Payments { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:BookingDb", ConnectionString);
        builder.UseSetting("ConnectionStrings:RabbitMq", RabbitMqConnectionString);
        builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(Time));
        builder.ConfigureTestServices(services => services.AddSingleton<Payments.PaymentsClient>(Payments));
    }

    public Bookings.BookingsClient CreateGrpcClient()
    {
        var channel = GrpcChannel.ForAddress(Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = Server.CreateHandler() });
        return new Bookings.BookingsClient(channel);
    }

    public BookingDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<BookingDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task DeleteBookingsAsync()
    {
        await using var db = CreateDbContext();
        await db.Bookings.ExecuteDeleteAsync();
        await db.OutboxMessages.ExecuteDeleteAsync();
    }

    // The single unpublished outbox message of the given type, deserialized.
    public async Task<T> SingleOutboxMessageAsync<T>(string type)
    {
        await using var db = CreateDbContext();
        var message = await db.OutboxMessages.SingleAsync(m => m.Type == type);
        return JsonSerializer.Deserialize<T>(message.Payload)!;
    }

    public async Task<int> CountOutboxMessagesAsync()
    {
        await using var db = CreateDbContext();
        return await db.OutboxMessages.CountAsync();
    }

    public async Task AddBookingsAsync(params Booking[] bookings)
    {
        await using var db = CreateDbContext();
        db.Bookings.AddRange(bookings);
        await db.SaveChangesAsync();
    }

    // Defaults: Hotel Aurora "Double Room" (id 2), 10.-12.11.2026, hold expires 30 min after creation.
    public Booking NewBooking(
        BookingStatus status,
        int roomTypeId = 2,
        string userId = "user_test",
        string checkIn = "2026-11-10",
        string checkOut = "2026-11-12",
        long totalPriceCents = 25800,
        TimeSpan? expiresIn = null,
        TimeSpan? createdAgo = null)
    {
        var now = Time.GetUtcNow();
        var createdAt = now - (createdAgo ?? TimeSpan.Zero);
        return new Booking
        {
            Id = Guid.CreateVersion7(),
            RoomTypeId = roomTypeId,
            ClerkUserId = userId,
            Email = $"{userId}@example.com",
            Status = status,
            CheckInDate = DateOnly.Parse(checkIn, CultureInfo.InvariantCulture),
            CheckOutDate = DateOnly.Parse(checkOut, CultureInfo.InvariantCulture),
            TotalPriceCents = totalPriceCents,
            ExpiresAt = expiresIn is { } e ? now + e : createdAt.AddMinutes(30),
            CreatedAt = createdAt
        };
    }

    public Task InitializeAsync() => Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbitMq.DisposeAsync();
    }
}
