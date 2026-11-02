using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace Gateway.Tests;

public class RateLimitTests : IClassFixture<RateLimitTests.LowLimitFactory>, IClassFixture<RateLimitTests.BehindProxyFactory>,
    IClassFixture<RateLimitTests.WithoutProxyFactory>
{
    private const string Url = "/api/search?city=Helsinki&checkIn=2026-11-10&checkOut=2026-11-12";

    public class LowLimitFactory : GatewayFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("RateLimit:RequestsPerMinute", "2");
        }
    }

    public class BehindProxyFactory : LowLimitFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("ForwardedHeaders:Enabled", "true");
        }
    }

    // Its own factory, so the test has its own rate limit counters.
    public class WithoutProxyFactory : LowLimitFactory;

    private readonly HttpClient _client;
    private readonly HttpClient _behindProxy;
    private readonly HttpClient _withoutProxy;

    public RateLimitTests(LowLimitFactory factory, BehindProxyFactory behindProxy, WithoutProxyFactory withoutProxy)
    {
        _client = factory.CreateClient();
        _behindProxy = behindProxy.CreateClient();
        _withoutProxy = withoutProxy.CreateClient();
    }

    [Fact]
    public async Task Rejects_requests_over_the_limit_with_429()
    {
        var first = await _client.GetAsync(Url);
        var second = await _client.GetAsync(Url);
        var third = await _client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task Health_check_needs_no_login_and_is_not_rate_limited()
    {
        for (var i = 0; i < 5; i++)
        {
            var response = await _client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Behind_a_proxy_each_client_ip_has_its_own_limit()
    {
        await GetFrom(_behindProxy, "203.0.113.1");
        await GetFrom(_behindProxy, "203.0.113.1");

        Assert.Equal(HttpStatusCode.TooManyRequests, (await GetFrom(_behindProxy, "203.0.113.1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetFrom(_behindProxy, "203.0.113.2")).StatusCode);
    }

    [Fact]
    public async Task Without_a_proxy_a_faked_header_does_not_get_around_the_limit()
    {
        await GetFrom(_withoutProxy, "198.51.100.1");
        await GetFrom(_withoutProxy, "198.51.100.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, (await GetFrom(_withoutProxy, "198.51.100.3")).StatusCode);
    }

    private static Task<HttpResponseMessage> GetFrom(HttpClient client, string ip)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Add("X-Forwarded-For", ip);
        return client.SendAsync(request);
    }
}
