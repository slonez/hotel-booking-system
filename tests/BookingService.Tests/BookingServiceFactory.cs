using BookingService.Data;
using Grpc.Net.Client;
using HotelBooking.Contracts.Booking;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;

namespace BookingService.Tests;

// Runs Booking Service in memory against a real PostgreSQL in Docker.
public class BookingServiceFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:BookingDb", ConnectionString);
        builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(Time));
    }

    public Bookings.BookingsClient CreateGrpcClient()
    {
        var channel = GrpcChannel.ForAddress(Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = Server.CreateHandler() });
        return new Bookings.BookingsClient(channel);
    }

    public BookingDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<BookingDbContext>().UseNpgsql(ConnectionString).Options);

    public Task InitializeAsync() => _postgres.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
