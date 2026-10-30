using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Gateway.Tests;

public class CreateBookingTests : IClassFixture<GatewayFactory>
{
    private readonly GatewayFactory _factory;
    private readonly HttpClient _client;

    public CreateBookingTests(GatewayFactory factory)
    {
        _factory = factory;
        _factory.Bookings.Error = null;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Creates_booking_for_the_user_in_the_token()
    {
        var response = await Book(GatewayFactory.CreateToken("user_123", "user@example.com"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/bookings/booking-1", response.Headers.Location?.ToString());

        var request = _factory.Bookings.LastCreate!;
        Assert.Equal("user_123", request.ClerkUserId);
        Assert.Equal("user@example.com", request.Email);
        Assert.Equal((2, "2026-11-10", "2026-11-12"), (request.RoomTypeId, request.CheckInDate, request.CheckOutDate));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("booking-1", body.GetProperty("bookingId").GetString());
        Assert.Equal(25800, body.GetProperty("totalPriceCents").GetInt64());
    }

    [Fact]
    public async Task Rejects_request_without_token()
    {
        var response = await Book(token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_token_signed_with_another_key()
    {
        var otherKey = new RsaSecurityKey(RSA.Create(2048));

        var response = await Book(GatewayFactory.CreateToken("user_123", "user@example.com", otherKey));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_token_without_email_claim()
    {
        var response = await Book(GatewayFactory.CreateToken("user_123", email: null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<HttpResponseMessage> Book(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/bookings")
        {
            Content = JsonContent.Create(new { roomTypeId = 2, checkIn = "2026-11-10", checkOut = "2026-11-12" })
        };
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await _client.SendAsync(request);
    }
}
