using Grpc.Core;
using HotelBooking.Contracts.Payment;

namespace BookingService.Tests;

// Stands in for Payment Service. Returns a fixed checkout URL or throws the given error.
public class FakePaymentsClient : Payments.PaymentsClient
{
    public const string CheckoutUrl = "https://checkout.stripe.com/c/pay/cs_test_1";

    public CreateCheckoutSessionRequest? LastRequest { get; private set; }
    public RpcException? Error { get; set; }

    public void Reset()
    {
        LastRequest = null;
        Error = null;
    }

    public override AsyncUnaryCall<CreateCheckoutSessionResponse> CreateCheckoutSessionAsync(
        CreateCheckoutSessionRequest request, CallOptions options)
    {
        LastRequest = request;
        return new AsyncUnaryCall<CreateCheckoutSessionResponse>(
            Error is null
                ? Task.FromResult(new CreateCheckoutSessionResponse { CheckoutUrl = CheckoutUrl })
                : Task.FromException<CreateCheckoutSessionResponse>(Error),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });
    }
}
