using System.Net;

namespace NotificationService.Tests;

public class HealthTests(NotificationServiceFactory factory) : IClassFixture<NotificationServiceFactory>
{
    [Fact]
    public async Task Is_healthy_when_database_and_rabbitmq_are_connected()
    {
        var client = factory.CreateClient();

        // The consumer connects to RabbitMQ in the background just after startup.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var response = await client.GetAsync("/health");
        while (response.StatusCode != HttpStatusCode.OK && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            response = await client.GetAsync("/health");
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
