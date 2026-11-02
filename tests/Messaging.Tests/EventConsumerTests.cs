using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using HotelBooking.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace Messaging.Tests;

public class RabbitMqFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

public record TestEvent(Guid Id);

// How many times each message reached the handler, and how many times each should fail first.
public class HandlerState
{
    public ConcurrentDictionary<Guid, int> Attempts { get; } = new();
    public ConcurrentDictionary<Guid, int> FailuresBeforeSuccess { get; } = new();
    public ConcurrentBag<Guid> Handled { get; } = [];
}

public class TestHandler(HandlerState state) : IEventHandler<TestEvent>
{
    public Task HandleAsync(TestEvent message, CancellationToken ct)
    {
        var attempt = state.Attempts.AddOrUpdate(message.Id, 1, (_, count) => count + 1);
        if (attempt <= state.FailuresBeforeSuccess.GetValueOrDefault(message.Id))
            throw new InvalidOperationException("Handler failed.");
        state.Handled.Add(message.Id);
        return Task.CompletedTask;
    }
}

public class EventConsumerTests(RabbitMqFixture rabbitMq) : IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private const int AlwaysFails = int.MaxValue;

    // Own queue and routing key per test, so tests sharing the broker don't see each other's messages.
    private readonly string _queue = $"test.{Guid.NewGuid():N}";
    private string RoutingKey => _queue;

    private readonly HandlerState _state = new();
    private IHost _host = null!;
    private IConnection _connection = null!;
    private IChannel _channel = null!;

    public async Task InitializeAsync()
    {
        _connection = await new ConnectionFactory { Uri = new Uri(rabbitMq.ConnectionString) }.CreateConnectionAsync();
        _channel = await _connection.CreateChannelAsync();
        await ConsumerQueue.DeclareAsync(_channel, _queue, RoutingKey);

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:RabbitMq"] = rabbitMq.ConnectionString,
            ["Messaging:RetryDelay"] = "00:00:00"
        });
        builder.Services.AddSingleton(_state);
        builder.Services.AddEventConsumer<TestEvent, TestHandler>(_queue, RoutingKey);
        _host = builder.Build();
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Message_that_fails_a_few_times_is_handled_and_not_dead_lettered()
    {
        var message = new TestEvent(Guid.NewGuid());
        _state.FailuresBeforeSuccess[message.Id] = 3;

        await PublishAsync(JsonSerializer.SerializeToUtf8Bytes(message));

        await WaitUntil(() => Task.FromResult(_state.Handled.Contains(message.Id)));
        Assert.Equal(4, _state.Attempts[message.Id]);
        Assert.Equal(0u, await DeadLetterCountAsync());
    }

    [Fact]
    public async Task Message_that_keeps_failing_moves_to_the_dead_letter_queue_and_keeps_its_content()
    {
        var message = new TestEvent(Guid.NewGuid());
        _state.FailuresBeforeSuccess[message.Id] = AlwaysFails;

        await PublishAsync(JsonSerializer.SerializeToUtf8Bytes(message));

        var dead = await WaitForDeadLetterAsync();
        Assert.Equal(message, JsonSerializer.Deserialize<TestEvent>(dead.Span));
        Assert.Equal(ConsumerQueue.MaxAttempts, _state.Attempts[message.Id]);
    }

    [Fact]
    public async Task Failing_message_does_not_block_the_next_one_after_dead_lettering()
    {
        var failing = new TestEvent(Guid.NewGuid());
        var next = new TestEvent(Guid.NewGuid());
        _state.FailuresBeforeSuccess[failing.Id] = AlwaysFails;

        await PublishAsync(JsonSerializer.SerializeToUtf8Bytes(failing));
        await PublishAsync(JsonSerializer.SerializeToUtf8Bytes(next));

        await WaitUntil(() => Task.FromResult(_state.Handled.Contains(next.Id)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task Malformed_message_goes_straight_to_the_dead_letter_queue(string body)
    {
        await PublishAsync(Encoding.UTF8.GetBytes(body));

        var dead = await WaitForDeadLetterAsync();
        Assert.Equal(body, Encoding.UTF8.GetString(dead.Span));
        Assert.Empty(_state.Attempts);
    }

    private async Task PublishAsync(byte[] body) =>
        await _channel.BasicPublishAsync(Exchanges.Events, RoutingKey, body);

    private Task<uint> DeadLetterCountAsync() =>
        _channel.MessageCountAsync(ConsumerQueue.DeadLetterName(_queue));

    private async Task<ReadOnlyMemory<byte>> WaitForDeadLetterAsync()
    {
        BasicGetResult? result = null;
        await WaitUntil(async () =>
        {
            result = await _channel.BasicGetAsync(ConsumerQueue.DeadLetterName(_queue), autoAck: true);
            return result is not null;
        });
        return result!.Body;
    }

    // Messages are handled in the background, so poll until the condition holds or time runs out.
    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            if (await condition())
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met within 15 seconds.");
            await Task.Delay(50);
        }
    }
}
