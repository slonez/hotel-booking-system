using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using HotelBooking.Contracts.Booking;

namespace Gateway.Tests;

public class SearchTests : IClassFixture<GatewayFactory>
{
    private readonly GatewayFactory _factory;
    private readonly HttpClient _client;

    public SearchTests(GatewayFactory factory)
    {
        _factory = factory;
        _factory.Bookings.Error = null;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Forwards_query_to_booking_service_and_returns_room_types()
    {
        _factory.Bookings.SearchResponse = new SearchAvailabilityResponse
        {
            RoomTypes =
            {
                new RoomTypeAvailability
                {
                    HotelId = 1, HotelName = "Hotel Aurora", RoomTypeId = 2, RoomTypeName = "Double Room",
                    PricePerNightCents = 12900, TotalPriceCents = 25800, AvailableRooms = 3
                }
            }
        };

        var response = await _client.GetAsync("/api/search?city=Helsinki&checkIn=2026-11-10&checkOut=2026-11-12");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var search = _factory.Bookings.LastSearch!;
        Assert.Equal(("Helsinki", "2026-11-10", "2026-11-12"), (search.City, search.CheckInDate, search.CheckOutDate));

        var roomType = (await response.Content.ReadFromJsonAsync<JsonElement>())[0];
        Assert.Equal("Hotel Aurora", roomType.GetProperty("hotelName").GetString());
        Assert.Equal(25800, roomType.GetProperty("totalPriceCents").GetInt64());
        Assert.Equal(3, roomType.GetProperty("availableRooms").GetInt32());
    }

    [Theory]
    [InlineData(StatusCode.InvalidArgument, HttpStatusCode.BadRequest)]
    [InlineData(StatusCode.NotFound, HttpStatusCode.NotFound)]
    [InlineData(StatusCode.FailedPrecondition, HttpStatusCode.Conflict)]
    [InlineData(StatusCode.Unavailable, HttpStatusCode.ServiceUnavailable)]
    [InlineData(StatusCode.DeadlineExceeded, HttpStatusCode.GatewayTimeout)]
    [InlineData(StatusCode.Internal, HttpStatusCode.InternalServerError)]
    public async Task Maps_grpc_errors_to_problem_details(StatusCode grpcStatus, HttpStatusCode httpStatus)
    {
        _factory.Bookings.Error = new RpcException(new Status(grpcStatus, "Backend message"));

        var response = await _client.GetAsync("/api/search?city=Helsinki&checkIn=2026-11-10&checkOut=2026-11-12");

        Assert.Equal(httpStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Shows_backend_message_for_client_errors_only()
    {
        _factory.Bookings.Error = new RpcException(new Status(StatusCode.InvalidArgument, "Check-out must be after check-in."));
        var badRequest = await GetProblem();

        _factory.Bookings.Error = new RpcException(new Status(StatusCode.Internal, "NullReferenceException at ..."));
        var serverError = await GetProblem();

        Assert.Equal("Check-out must be after check-in.", badRequest.GetProperty("detail").GetString());
        Assert.False(serverError.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task Metrics_count_errors_with_the_status_code_the_client_got()
    {
        _factory.Bookings.Error = new RpcException(new Status(StatusCode.Internal, "Failed"));
        await GetProblem();

        var metrics = await _client.GetStringAsync("/metrics");

        // The exception handler clears the route, so the endpoint is empty. The GatewayErrors alert relies on this form.
        Assert.Contains(metrics.Split('\n'), line => line.StartsWith("http_requests_received_total{")
            && line.Contains("code=\"500\"") && line.Contains("endpoint=\"\""));
    }

    private async Task<JsonElement> GetProblem()
    {
        var response = await _client.GetAsync("/api/search?city=Helsinki&checkIn=2026-11-10&checkOut=2026-11-12");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
