using System.Collections.Concurrent;
using Stripe;
using Stripe.Checkout;

namespace PaymentService.Tests;

// Stands in for Stripe. Records the options and returns a new session for each call.
public class FakeSessionService : SessionService
{
    private int _count;

    public ConcurrentQueue<SessionCreateOptions> Created { get; } = new();

    public override async Task<Session> CreateAsync(
        SessionCreateOptions options, RequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
    {
        Created.Enqueue(options);
        // Like a real network call, so concurrent requests overlap.
        await Task.Delay(50, cancellationToken);
        var id = $"cs_test_{Interlocked.Increment(ref _count)}";
        return new Session { Id = id, Url = $"https://checkout.stripe.com/c/pay/{id}" };
    }
}
