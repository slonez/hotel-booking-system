using System.Text.Json;
using HotelBooking.Contracts.Events;
using HotelBooking.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentService.Data;
using RabbitMQ.Client;
using Stripe;
using Refund = PaymentService.Data.Refund;

namespace PaymentService.Tests;

[Collection(PaymentServiceCollection.Name)]
public class RefundTests(PaymentServiceFactory factory) : IAsyncLifetime
{
    private readonly Payment _payment = new()
    {
        Id = Guid.CreateVersion7(),
        BookingId = Guid.CreateVersion7(),
        StripeSessionId = "cs_test_refund",
        CheckoutUrl = "https://checkout.stripe.com/c/pay/cs_test_refund",
        StripePaymentIntentId = "pi_test_refund",
        AmountCents = 25800,
        Status = PaymentStatus.Succeeded
    };

    // Resolving the processor starts the service, which applies migrations.
    private readonly RefundProcessor _processor = factory.Services.GetRequiredService<RefundProcessor>();

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _payment.CreatedAt = factory.Time.GetUtcNow();
        await using var db = factory.CreateDbContext();
        db.Payments.Add(_payment);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Refund_request_saves_pending_refund_once()
    {
        await Handle(RefundRequest(12900));
        await Handle(RefundRequest(12900));

        var refund = Assert.Single(await GetRefunds());
        Assert.Equal((_payment.Id, 12900L, RefundReason.Cancelled, RefundStatus.Pending),
            (refund.PaymentId, refund.AmountCents, refund.Reason, refund.Status));
    }

    [Theory]
    [InlineData(PaymentStatus.Pending, 12900)]
    [InlineData(PaymentStatus.Succeeded, 25801)]
    [InlineData(PaymentStatus.Succeeded, 0)]
    public async Task Ignores_refund_request_that_does_not_match_the_payment(PaymentStatus status, long amountCents)
    {
        await using (var db = factory.CreateDbContext())
            await db.Payments.ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, status));

        await Handle(RefundRequest(amountCents));

        Assert.Empty(await GetRefunds());
    }

    [Fact]
    public async Task Processor_refunds_through_stripe_with_refund_id_as_idempotency_key()
    {
        await Handle(RefundRequest(12900));

        await _processor.ProcessPendingAsync(CancellationToken.None);

        var (options, requestOptions) = Assert.Single(factory.Refunds.Created);
        var refund = Assert.Single(await GetRefunds());
        Assert.Equal(("pi_test_refund", 12900L), (options.PaymentIntent, options.Amount));
        Assert.Equal(refund.Id.ToString(), requestOptions?.IdempotencyKey);
        Assert.Equal((RefundStatus.Succeeded, "re_test_1"), (refund.Status, refund.StripeRefundId));

        // Not refunded again on the next run.
        Assert.Equal(0, await _processor.ProcessPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Processor_retries_failed_refund_and_gives_up_after_max_attempts()
    {
        await Handle(RefundRequest(12900));
        factory.Refunds.Error = new StripeException("Stripe is down");

        await _processor.ProcessPendingAsync(CancellationToken.None);
        Assert.Equal((RefundStatus.Pending, 1), Status(Assert.Single(await GetRefunds())));

        for (var i = 1; i < RefundProcessor.MaxAttempts; i++)
            await _processor.ProcessPendingAsync(CancellationToken.None);
        Assert.Equal((RefundStatus.Failed, RefundProcessor.MaxAttempts), Status(Assert.Single(await GetRefunds())));
    }

    [Fact]
    public async Task Refund_request_published_to_rabbitmq_is_saved()
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(factory.RabbitMqConnectionString) }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        // Declared like the consumer does, so the message waits in the queue even if the consumer has not started yet.
        await channel.ExchangeDeclareAsync(Exchanges.Events, ExchangeType.Topic, durable: true);
        await channel.QueueDeclareAsync(RefundRequestedHandler.Queue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(RefundRequestedHandler.Queue, Exchanges.Events, RefundRequested.RoutingKey);

        await channel.BasicPublishAsync(Exchanges.Events, RefundRequested.RoutingKey,
            JsonSerializer.SerializeToUtf8Bytes(RefundRequest(12900)));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await GetRefunds()).Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "Refund was not saved within 10 seconds.");
            await Task.Delay(100);
        }
    }

    private RefundRequested RefundRequest(long amountCents) =>
        new(_payment.BookingId, "guest@example.com", "Hotel Aurora", "Double Room", amountCents, RefundReason.Cancelled);

    private async Task Handle(RefundRequested message)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RefundRequestedHandler>().HandleAsync(message, CancellationToken.None);
    }

    private async Task<List<Refund>> GetRefunds()
    {
        await using var db = factory.CreateDbContext();
        return await db.Refunds.ToListAsync();
    }

    private static (RefundStatus, int) Status(Refund refund) => (refund.Status, refund.Attempts);
}
