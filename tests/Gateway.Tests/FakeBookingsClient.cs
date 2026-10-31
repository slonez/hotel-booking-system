using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using HotelBooking.Contracts.Booking;

namespace Gateway.Tests;

// Stands in for Booking Service. Returns a canned response or throws the given error.
public class FakeBookingsClient : Bookings.BookingsClient
{
    public SearchAvailabilityRequest? LastSearch { get; private set; }
    public SearchAvailabilityResponse SearchResponse { get; set; } = new();
    public CreateBookingRequest? LastCreate { get; private set; }
    public ListBookingsRequest? LastList { get; private set; }
    public ListBookingsResponse ListResponse { get; set; } = new();
    public CancelBookingRequest? LastCancel { get; private set; }
    public StartPaymentRequest? LastStartPayment { get; private set; }
    public CallOptions LastStartPaymentOptions { get; private set; }
    public RpcException? Error { get; set; }

    public override AsyncUnaryCall<SearchAvailabilityResponse> SearchAvailabilityAsync(
        SearchAvailabilityRequest request, CallOptions options)
    {
        LastSearch = request;
        return Call(SearchResponse);
    }

    public override AsyncUnaryCall<CreateBookingResponse> CreateBookingAsync(
        CreateBookingRequest request, CallOptions options)
    {
        LastCreate = request;
        return Call(new CreateBookingResponse
        {
            BookingId = "booking-1",
            TotalPriceCents = 25800,
            ExpiresAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 11, 1, 12, 30, 0, TimeSpan.Zero))
        });
    }

    public override AsyncUnaryCall<ListBookingsResponse> ListBookingsAsync(
        ListBookingsRequest request, CallOptions options)
    {
        LastList = request;
        return Call(ListResponse);
    }

    public override AsyncUnaryCall<CancelBookingResponse> CancelBookingAsync(
        CancelBookingRequest request, CallOptions options)
    {
        LastCancel = request;
        return Call(new CancelBookingResponse { RefundAmountCents = 12900 });
    }

    public override AsyncUnaryCall<StartPaymentResponse> StartPaymentAsync(
        StartPaymentRequest request, CallOptions options)
    {
        LastStartPayment = request;
        LastStartPaymentOptions = options;
        return Call(new StartPaymentResponse { CheckoutUrl = "https://checkout.stripe.com/c/pay/cs_test_1" });
    }

    private AsyncUnaryCall<T> Call<T>(T response) => new(
        Error is null ? Task.FromResult(response) : Task.FromException<T>(Error),
        Task.FromResult(new Metadata()),
        () => Status.DefaultSuccess,
        () => [],
        () => { });
}
