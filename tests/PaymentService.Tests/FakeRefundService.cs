using Stripe;

namespace PaymentService.Tests;

// Stands in for Stripe refunds. Records each call or throws the given error.
public class FakeRefundService : RefundService
{
    public List<(RefundCreateOptions Options, RequestOptions? RequestOptions)> Created { get; } = [];
    public StripeException? Error { get; set; }

    public void Reset()
    {
        Created.Clear();
        Error = null;
    }

    public override Task<Refund> CreateAsync(
        RefundCreateOptions options, RequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
    {
        if (Error is not null)
            throw Error;
        Created.Add((options, requestOptions));
        return Task.FromResult(new Refund { Id = $"re_test_{Created.Count}", Status = "succeeded" });
    }
}
