using System.Net;
using System.Text;
using System.Text.Json;
using HotelBooking.Contracts.Events;
using HotelBooking.Messaging;
using Microsoft.EntityFrameworkCore;
using PaymentService.Data;
using Stripe;

namespace PaymentService.Tests;

[Collection(PaymentServiceCollection.Name)]
public class StripeWebhookTests(PaymentServiceFactory factory) : IAsyncLifetime
{
    private const string SessionId = "cs_test_webhook";

    private readonly HttpClient _http = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        await using var db = factory.CreateDbContext();
        db.Payments.Add(new Payment
        {
            Id = Guid.CreateVersion7(),
            BookingId = Guid.CreateVersion7(),
            StripeSessionId = SessionId,
            CheckoutUrl = $"https://checkout.stripe.com/c/pay/{SessionId}",
            AmountCents = 25800,
            Status = PaymentStatus.Pending,
            CreatedAt = factory.Time.GetUtcNow()
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(EventTypes.CheckoutSessionCompleted, "paid", PaymentStatus.Succeeded)]
    [InlineData(EventTypes.CheckoutSessionAsyncPaymentSucceeded, "paid", PaymentStatus.Succeeded)]
    [InlineData(EventTypes.CheckoutSessionExpired, "unpaid", PaymentStatus.Expired)]
    // Delayed payment methods: completed but not paid yet.
    [InlineData(EventTypes.CheckoutSessionCompleted, "unpaid", PaymentStatus.Pending)]
    public async Task Updates_payment_status_from_event(string type, string paymentStatus, PaymentStatus expected)
    {
        var response = await SendEvent(type, paymentStatus: paymentStatus);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await GetStatus());
    }

    [Fact]
    public async Task Succeeded_payment_saves_payment_succeeded_event_to_outbox()
    {
        await SendEvent(EventTypes.CheckoutSessionCompleted);

        await using var db = factory.CreateDbContext();
        var payment = await db.Payments.SingleAsync();
        Assert.Equal("pi_test_1", payment.StripePaymentIntentId);
        var message = await db.OutboxMessages.SingleAsync();
        Assert.Equal(PaymentSucceeded.RoutingKey, message.Type);
        Assert.Null(message.PublishedAt);
        Assert.Equal(new PaymentSucceeded(payment.BookingId, payment.Id, 25800),
            JsonSerializer.Deserialize<PaymentSucceeded>(message.Payload));
    }

    [Fact]
    public async Task Expired_payment_saves_no_event()
    {
        await SendEvent(EventTypes.CheckoutSessionExpired);

        await using var db = factory.CreateDbContext();
        Assert.False(await db.OutboxMessages.AnyAsync());
    }

    [Fact]
    public async Task Duplicate_event_is_acknowledged_without_changes()
    {
        var eventJson = EventJson(EventTypes.CheckoutSessionCompleted, SessionId, "paid");

        var first = await Send(eventJson);
        var second = await Send(eventJson);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (first.StatusCode, second.StatusCode));
        Assert.Equal(PaymentStatus.Succeeded, await GetStatus());
        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Late_expired_event_does_not_undo_a_succeeded_payment()
    {
        await SendEvent(EventTypes.CheckoutSessionCompleted);

        var response = await SendEvent(EventTypes.CheckoutSessionExpired);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PaymentStatus.Succeeded, await GetStatus());
    }

    [Fact]
    public async Task Rejects_event_with_invalid_signature()
    {
        var response = await Send(EventJson(EventTypes.CheckoutSessionCompleted, SessionId, "paid"), secret: "whsec_wrong");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PaymentStatus.Pending, await GetStatus());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-signature")]
    public async Task Rejects_event_with_missing_or_malformed_signature_header(string? header)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/stripe/webhook")
        {
            Content = new StringContent(EventJson(EventTypes.CheckoutSessionCompleted, SessionId, "paid"), Encoding.UTF8, "application/json")
        };
        if (header is not null)
            request.Headers.Add("Stripe-Signature", header);

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PaymentStatus.Pending, await GetStatus());
    }

    [Fact]
    public async Task Acknowledges_event_for_unknown_session()
    {
        var response = await SendEvent(EventTypes.CheckoutSessionCompleted, sessionId: "cs_test_unknown");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PaymentStatus.Pending, await GetStatus());
    }

    private Task<HttpResponseMessage> SendEvent(string type, string sessionId = SessionId, string paymentStatus = "paid") =>
        Send(EventJson(type, sessionId, paymentStatus));

    private async Task<HttpResponseMessage> Send(string json, string secret = PaymentServiceFactory.WebhookSecret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/stripe/webhook")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Stripe-Signature", EventUtility.GenerateSignatureHeader(json, secret));
        return await _http.SendAsync(request);
    }

    // Minimal Stripe event. The API version must match the SDK or ConstructEvent rejects it.
    private static string EventJson(string type, string sessionId, string paymentStatus) =>
        JsonSerializer.Serialize(new
        {
            id = $"evt_{Guid.NewGuid():N}",
            @object = "event",
            api_version = StripeConfiguration.ApiVersion,
            type,
            data = new { @object = new { id = sessionId, @object = "checkout.session", payment_status = paymentStatus, payment_intent = "pi_test_1" } }
        });

    private async Task<PaymentStatus> GetStatus()
    {
        await using var db = factory.CreateDbContext();
        return await db.Payments.Where(p => p.StripeSessionId == SessionId).Select(p => p.Status).SingleAsync();
    }
}
