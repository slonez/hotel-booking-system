using System.Security.Cryptography;
using HotelBooking.Contracts.Booking;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Gateway.Tests;

public class GatewayFactory : WebApplicationFactory<Program>
{
    private const string Issuer = "https://test.clerk.accounts.dev";
    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048));

    public FakeBookingsClient Bookings { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<Bookings.BookingsClient>(Bookings);

            // Trust tokens signed with the test key instead of fetching Clerk's keys.
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                options.Configuration.SigningKeys.Add(SigningKey);
            });
        });

    public static string CreateToken(string userId, string? email, SecurityKey? signingKey = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = userId };
        if (email is not null)
            claims["email"] = email;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(signingKey ?? SigningKey, SecurityAlgorithms.RsaSha256)
        });
    }
}
