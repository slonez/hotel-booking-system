using System.Net;
using System.Net.Sockets;
using HotelBooking.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Messaging.Tests;

public class RecoveryDbContext(DbContextOptions<RecoveryDbContext> options) : DbContext(options)
{
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ConfigureOutbox();
}

// Never finishes on its own, like a service that crashes in the middle of handling a message.
public class StuckHandler(HandlerState state) : IEventHandler<TestEvent>
{
    public async Task HandleAsync(TestEvent message, CancellationToken ct)
    {
        state.Attempts.AddOrUpdate(message.Id, 1, (_, count) => count + 1);
        await Task.Delay(Timeout.Infinite, ct);
    }
}

// The whole path from outbox to handler with outages in between. Each test gets its own containers,
// because they stop RabbitMQ. A fixed port keeps the address the same across a restart.
public class RecoveryTests : IAsyncLifetime
{
    private const string Queue = "test.recovery";
    private const string RoutingKey = "test.recovery";

    private readonly int _rabbitMqPort = GetFreePort();
    private readonly RabbitMqContainer _rabbitMq;
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();
    private readonly HandlerState _state = new();

    public RecoveryTests() =>
        _rabbitMq = new RabbitMqBuilder("rabbitmq:4").WithPortBinding(_rabbitMqPort, 5672).Build();

    private string RabbitMqConnectionString => $"amqp://rabbitmq:rabbitmq@localhost:{_rabbitMqPort}";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_rabbitMq.StartAsync(), _postgres.StartAsync());
        await using var db = CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _rabbitMq.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Message_is_delivered_again_after_a_crash_during_handling()
    {
        var message = new TestEvent(Guid.NewGuid());
        await AddToOutboxAsync(message);

        using (var crashing = await StartAsync<StuckHandler>())
        {
            await WaitUntil(() => _state.Attempts.ContainsKey(message.Id));
            await crashing.StopAsync();
        }

        using var restarted = await StartAsync<TestHandler>();
        await WaitUntil(() => _state.Handled.Contains(message.Id));
    }

    [Fact]
    public async Task Messages_flow_again_after_rabbitmq_restarts()
    {
        using var host = await StartAsync<TestHandler>();
        var before = new TestEvent(Guid.NewGuid());
        await AddToOutboxAsync(before);
        await WaitUntil(() => _state.Handled.Contains(before.Id));

        await _rabbitMq.StopAsync();
        var during = new TestEvent(Guid.NewGuid());
        await AddToOutboxAsync(during);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(1, await CountUnpublishedAsync());

        await _rabbitMq.StartAsync();
        await WaitUntil(() => _state.Handled.Contains(during.Id));
        Assert.Equal(0, await CountUnpublishedAsync());
    }

    [Fact]
    public async Task Service_started_during_a_rabbitmq_outage_starts_working_when_it_returns()
    {
        await _rabbitMq.StopAsync();
        var message = new TestEvent(Guid.NewGuid());
        await AddToOutboxAsync(message);

        using var host = await StartAsync<TestHandler>();
        await Task.Delay(TimeSpan.FromSeconds(2));

        await _rabbitMq.StartAsync();
        await WaitUntil(() => _state.Handled.Contains(message.Id));
    }

    [Fact]
    public async Task Queued_message_survives_a_rabbitmq_restart_while_the_consumer_is_down()
    {
        var message = new TestEvent(Guid.NewGuid());
        await AddToOutboxAsync(message);

        // Publishes the message and stops before it has been handled.
        using (var crashing = await StartAsync<StuckHandler>())
        {
            await WaitUntil(() => _state.Attempts.ContainsKey(message.Id));
            await crashing.StopAsync();
        }
        Assert.Equal(0, await CountUnpublishedAsync());

        await _rabbitMq.StopAsync();
        await _rabbitMq.StartAsync();

        using var restarted = await StartAsync<TestHandler>();
        await WaitUntil(() => _state.Handled.Contains(message.Id));
    }

    // The manual procedure documented in the README.
    [Fact]
    public async Task Dead_letter_moved_back_with_a_shovel_is_handled()
    {
        var message = new TestEvent(Guid.NewGuid());
        // Fails until it is dead lettered, then succeeds once the fault has been "fixed".
        _state.FailuresBeforeSuccess[message.Id] = ConsumerQueue.MaxAttempts;
        using var host = await StartAsync<TestHandler>();
        await AddToOutboxAsync(message);
        await WaitUntilDeadLetteredAsync();
        Assert.Empty(_state.Handled);

        await ExecInRabbitMqAsync("rabbitmq-plugins", "enable", "rabbitmq_shovel");
        await ExecInRabbitMqAsync("rabbitmqctl", "set_parameter", "shovel", "replay-dead-letters", $$"""
            {"src-protocol":"amqp091","src-uri":"amqp://","src-queue":"{{ConsumerQueue.DeadLetterName(Queue)}}","src-delete-after":"queue-length",
             "dest-protocol":"amqp091","dest-uri":"amqp://","dest-queue":"{{Queue}}"}
            """);

        await WaitUntil(() => _state.Handled.Contains(message.Id));

        // The shovel removes itself once the queue is empty.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await _rabbitMq.ExecAsync(["rabbitmqctl", "-q", "list_parameters"])).Stdout.Contains("replay-dead-letters"))
        {
            Assert.True(DateTime.UtcNow < deadline, "Shovel was not removed within 30 seconds.");
            await Task.Delay(200);
        }
    }

    private async Task ExecInRabbitMqAsync(params string[] command)
    {
        var result = await _rabbitMq.ExecAsync(command);
        Assert.True(result.ExitCode == 0, result.Stderr);
    }

    private async Task WaitUntilDeadLetteredAsync()
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(RabbitMqConnectionString) }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        // Same declaration as the consumer, so the queue exists whichever gets here first.
        await ConsumerQueue.DeclareAsync(channel, Queue, RoutingKey);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (await channel.MessageCountAsync(ConsumerQueue.DeadLetterName(Queue)) == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "Message was not dead lettered within 60 seconds.");
            await Task.Delay(100);
        }
    }

    private async Task<IHost> StartAsync<THandler>() where THandler : class, IEventHandler<TestEvent>
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:RabbitMq"] = RabbitMqConnectionString,
            ["Messaging:RetryDelay"] = "00:00:00.200"
        });
        builder.Services.AddSingleton(_state);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddDbContext<RecoveryDbContext>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.AddOutboxPublisher<RecoveryDbContext>();
        builder.Services.AddEventConsumer<TestEvent, THandler>(Queue, RoutingKey);
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private RecoveryDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<RecoveryDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options);

    private async Task AddToOutboxAsync(TestEvent message)
    {
        await using var db = CreateDbContext();
        db.OutboxMessages.Add(OutboxMessage.Create(RoutingKey, message, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task<int> CountUnpublishedAsync()
    {
        await using var db = CreateDbContext();
        return await db.OutboxMessages.CountAsync(m => m.PublishedAt == null);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    // Recovery takes time: the RabbitMQ client tries to reconnect every 5 seconds.
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met within 60 seconds.");
            await Task.Delay(100);
        }
    }
}
