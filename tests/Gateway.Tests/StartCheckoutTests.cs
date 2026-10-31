using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gateway.Tests;

public class StartCheckoutTests : IClassFixture<GatewayFactory>
{
    private readonly GatewayFactory _factory;
    private readonly HttpClient _client;

    public StartCheckoutTests(GatewayFactory factory)
    {
        _factory = factory;
        _factory.Bookings.Error = null;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Returns_checkout_url_for_the_user_in_the_token()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/bookings/booking-1/checkout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GatewayFactory.CreateToken("user_123", email: null));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var startPayment = _factory.Bookings.LastStartPayment!;
        Assert.Equal(("booking-1", "user_123"), (startPayment.BookingId, startPayment.ClerkUserId));
        // Longer than the 5 s default, because Booking calls Stripe through Payment Service.
        Assert.InRange(_factory.Bookings.LastStartPaymentOptions.Deadline!.Value - DateTime.UtcNow,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://checkout.stripe.com/c/pay/cs_test_1", body.GetProperty("checkoutUrl").GetString());
    }

    [Fact]
    public async Task Rejects_request_without_token()
    {
        var response = await _client.PostAsync("/api/bookings/booking-1/checkout", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
