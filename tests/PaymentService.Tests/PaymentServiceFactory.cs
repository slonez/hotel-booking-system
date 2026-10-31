using Grpc.Net.Client;
using HotelBooking.Contracts.Payment;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PaymentService.Data;
using Stripe.Checkout;
using Testcontainers.PostgreSql;

namespace PaymentService.Tests;

// All integration test classes share one service and one PostgreSQL container.
// Classes in the same collection run one after another, so they don't see each other's data.
[CollectionDefinition(Name)]
public class PaymentServiceCollection : ICollectionFixture<PaymentServiceFactory>
{
    public const string Name = "PaymentService";
}

// Runs Payment Service in memory against a real PostgreSQL in Docker and a fake Stripe.
public class PaymentServiceFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    // Replaced before each test so tests don't see each other's Stripe calls.
    public FakeSessionService Sessions { get; private set; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:PaymentDb", ConnectionString);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);
            services.AddScoped<SessionService>(_ => Sessions);
        });
    }

    public Payments.PaymentsClient CreateGrpcClient()
    {
        var channel = GrpcChannel.ForAddress(Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = Server.CreateHandler() });
        return new Payments.PaymentsClient(channel);
    }

    public PaymentDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<PaymentDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task ResetAsync()
    {
        Sessions = new FakeSessionService();
        await using var db = CreateDbContext();
        await db.Payments.ExecuteDeleteAsync();
    }

    public Task InitializeAsync() => _postgres.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
