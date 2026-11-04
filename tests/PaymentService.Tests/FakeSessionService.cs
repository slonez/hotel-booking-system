using System.Collections.Concurrent;
using Stripe;
using Stripe.Checkout;

namespace PaymentService.Tests;

// Stands in for Stripe. Records the options and returns a new session for each call.
public class FakeSessionService : SessionService
{
    private int _count;

    public ConcurrentQueue<SessionCreateOptions> Created { get; } = new();

    public StripeException? Error { get; set; }

    // What Stripe reports for each session ID. A session listed here with no value fails with a StripeException.
    public ConcurrentDictionary<string, Session?> Existing { get; } = new();

    public ConcurrentQueue<string> Fetched { get; } = new();

    public override Task<Session> GetAsync(
        string id, SessionGetOptions? options = null, RequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
    {
        Fetched.Enqueue(id);
        return Existing[id] is { } session
            ? Task.FromResult(session)
            : throw new StripeException("Stripe is unavailable.");
    }

    public override async Task<Session> CreateAsync(
        SessionCreateOptions options, RequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
    {
        if (Error is not null)
            throw Error;
        Created.Enqueue(options);
        // Like a real network call, so concurrent requests overlap.
        await Task.Delay(50, cancellationToken);
        var id = $"cs_test_{Interlocked.Increment(ref _count)}";
        return new Session { Id = id, Url = $"https://checkout.stripe.com/c/pay/{id}" };
    }
}
