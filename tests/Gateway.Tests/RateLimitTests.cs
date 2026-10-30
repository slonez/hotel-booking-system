using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace Gateway.Tests;

public class RateLimitTests : IClassFixture<RateLimitTests.LowLimitFactory>
{
    public class LowLimitFactory : GatewayFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("RateLimit:RequestsPerMinute", "2");
        }
    }

    private readonly HttpClient _client;

    public RateLimitTests(LowLimitFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Rejects_requests_over_the_limit_with_429()
    {
        const string url = "/api/search?city=Helsinki&checkIn=2026-11-10&checkOut=2026-11-12";

        var first = await _client.GetAsync(url);
        var second = await _client.GetAsync(url);
        var third = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }
}
