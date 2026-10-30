using HotelBooking.Contracts.Booking;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Tests;

public class GatewayFactory : WebApplicationFactory<Program>
{
    public FakeBookingsClient Bookings { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
            services.AddSingleton<Bookings.BookingsClient>(Bookings));
}
