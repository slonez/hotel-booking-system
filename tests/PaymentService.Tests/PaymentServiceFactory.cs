using Grpc.Net.Client;
using HotelBooking.Contracts.Payment;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PaymentService.Data;
using Stripe;
using Stripe.Checkout;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace PaymentService.Tests;

// All integration test classes share one service and one PostgreSQL container.
// Classes in the same collection run one after another, so they don't see each other's data.
[CollectionDefinition(Name)]
public class PaymentServiceCollection : ICollectionFixture<PaymentServiceFactory>
{
    public const string Name = "PaymentService";
}

// Runs Payment Service in memory against real PostgreSQL and RabbitMQ in Docker and a fake Stripe.
public class PaymentServiceFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string WebhookSecret = "whsec_test";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public string RabbitMqConnectionString => _rabbitMq.GetConnectionString();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    // Replaced before each test so tests don't see each other's Stripe calls.
    public FakeSessionService Sessions { get; private set; } = new();
    // RefundProcessor is a singleton, so this fake is reset instead of replaced.
    public FakeRefundService Refunds { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:PaymentDb", ConnectionString);
        builder.UseSetting("Stripe:WebhookSecret", WebhookSecret);
        builder.UseSetting("ConnectionStrings:RabbitMq", RabbitMqConnectionString);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);
            services.AddScoped<SessionService>(_ => Sessions);
            services.AddSingleton<RefundService>(Refunds);
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
        Refunds.Reset();
        await using var db = CreateDbContext();
        await db.Refunds.ExecuteDeleteAsync();
        await db.Payments.ExecuteDeleteAsync();
        await db.OutboxMessages.ExecuteDeleteAsync();
    }

    public Task InitializeAsync() => Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbitMq.DisposeAsync();
    }
}
