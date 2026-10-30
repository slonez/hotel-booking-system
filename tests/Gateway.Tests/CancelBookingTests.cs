using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gateway.Tests;

public class CancelBookingTests : IClassFixture<GatewayFactory>
{
    private readonly GatewayFactory _factory;
    private readonly HttpClient _client;

    public CancelBookingTests(GatewayFactory factory)
    {
        _factory = factory;
        _factory.Bookings.Error = null;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Cancels_booking_as_the_user_in_the_token()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/bookings/booking-1/cancel");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GatewayFactory.CreateToken("user_123", email: null));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cancel = _factory.Bookings.LastCancel!;
        Assert.Equal(("booking-1", "user_123"), (cancel.BookingId, cancel.ClerkUserId));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(12900, body.GetProperty("refundAmountCents").GetInt64());
    }

    [Fact]
    public async Task Rejects_request_without_token()
    {
        var response = await _client.PostAsync("/api/bookings/booking-1/cancel", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
