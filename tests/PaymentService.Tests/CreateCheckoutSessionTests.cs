using Grpc.Core;
using HotelBooking.Contracts.Payment;
using Microsoft.EntityFrameworkCore;
using PaymentService.Data;

namespace PaymentService.Tests;

// Test time is 2026-11-01 12:00 UTC.
[Collection(PaymentServiceCollection.Name)]
public class CreateCheckoutSessionTests(PaymentServiceFactory factory) : IAsyncLifetime
{
    private static readonly Guid BookingId = Guid.CreateVersion7();

    // Creating the client starts the service, which applies migrations.
    private readonly Payments.PaymentsClient _client = factory.CreateGrpcClient();

    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Creates_stripe_session_and_pending_payment()
    {
        var response = await CreateSession();

        var options = Assert.Single(factory.Sessions.Created);
        Assert.Equal(BookingId.ToString(), options.ClientReferenceId);
        Assert.Equal("guest@example.com", options.CustomerEmail);
        Assert.Equal(factory.Time.GetUtcNow().AddMinutes(30).UtcDateTime, options.ExpiresAt);
        var price = Assert.Single(options.LineItems).PriceData;
        Assert.Equal(("eur", 25800L), (price.Currency, price.UnitAmount));

        await using var db = factory.CreateDbContext();
        var payment = await db.Payments.SingleAsync();
        Assert.Equal((BookingId, PaymentStatus.Pending, 25800L), (payment.BookingId, payment.Status, payment.AmountCents));
        Assert.Equal(payment.CheckoutUrl, response.CheckoutUrl);
    }

    [Fact]
    public async Task Calling_again_returns_the_same_session()
    {
        var first = await CreateSession();
        var second = await CreateSession();

        Assert.Equal(first.CheckoutUrl, second.CheckoutUrl);
        Assert.Single(factory.Sessions.Created);
    }

    [Fact]
    public async Task Concurrent_calls_return_one_checkout_url()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => CreateSession()));

        Assert.Single(responses.Select(r => r.CheckoutUrl).Distinct());
        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.Payments.CountAsync());
    }

    [Theory]
    [InlineData(PaymentStatus.Succeeded)]
    [InlineData(PaymentStatus.Expired)]
    public async Task Rejects_booking_whose_payment_is_no_longer_pending(PaymentStatus status)
    {
        await CreateSession();
        await using (var db = factory.CreateDbContext())
            await db.Payments.ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, status));

        var ex = await Assert.ThrowsAsync<RpcException>(() => CreateSession());

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid", 25800)]
    [InlineData("0199f2a4-0000-7000-8000-000000000000", 0)]
    public async Task Rejects_invalid_input(string bookingId, long amountCents)
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() => CreateSession(bookingId, amountCents));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    private async Task<CreateCheckoutSessionResponse> CreateSession(string? bookingId = null, long amountCents = 25800) =>
        await _client.CreateCheckoutSessionAsync(new CreateCheckoutSessionRequest
        {
            BookingId = bookingId ?? BookingId.ToString(),
            AmountCents = amountCents,
            Description = "Hotel Aurora, Double Room, 2026-11-10 – 2026-11-12",
            CustomerEmail = "guest@example.com"
        });
}
