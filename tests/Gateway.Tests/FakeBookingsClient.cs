using Grpc.Core;
using HotelBooking.Contracts.Booking;

namespace Gateway.Tests;

// Stands in for Booking Service. Returns a canned response or throws the given error.
public class FakeBookingsClient : Bookings.BookingsClient
{
    public SearchAvailabilityRequest? LastSearch { get; private set; }
    public SearchAvailabilityResponse SearchResponse { get; set; } = new();
    public RpcException? Error { get; set; }

    public override AsyncUnaryCall<SearchAvailabilityResponse> SearchAvailabilityAsync(
        SearchAvailabilityRequest request, CallOptions options)
    {
        LastSearch = request;
        return Call(SearchResponse);
    }

    private AsyncUnaryCall<T> Call<T>(T response) => new(
        Error is null ? Task.FromResult(response) : Task.FromException<T>(Error),
        Task.FromResult(new Metadata()),
        () => Status.DefaultSuccess,
        () => [],
        () => { });
}
