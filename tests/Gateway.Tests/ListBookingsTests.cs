using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using HotelBooking.Contracts.Booking;

namespace Gateway.Tests;

public class ListBookingsTests : IClassFixture<GatewayFactory>
{
    private readonly GatewayFactory _factory;
    private readonly HttpClient _client;

    public ListBookingsTests(GatewayFactory factory)
    {
        _factory = factory;
        _factory.Bookings.Error = null;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Lists_bookings_of_the_user_in_the_token()
    {
        _factory.Bookings.ListResponse = new ListBookingsResponse
        {
            Bookings =
            {
                new BookingSummary
                {
                    BookingId = "booking-1", HotelName = "Hotel Aurora", RoomTypeName = "Double Room",
                    CheckInDate = "2026-11-10", CheckOutDate = "2026-11-12", Status = "Held",
                    TotalPriceCents = 25800, ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch)
                }
            }
        };
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/bookings");
        // No email claim needed for listing.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GatewayFactory.CreateToken("user_123", email: null));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("user_123", _factory.Bookings.LastList!.ClerkUserId);
        var booking = (await response.Content.ReadFromJsonAsync<JsonElement>())[0];
        Assert.Equal("Held", booking.GetProperty("status").GetString());
        Assert.Equal("2026-11-10", booking.GetProperty("checkIn").GetString());
    }

    [Fact]
    public async Task Rejects_request_without_token()
    {
        var response = await _client.GetAsync("/api/bookings");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
