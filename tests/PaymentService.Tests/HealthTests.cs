using System.Net;

namespace PaymentService.Tests;

[Collection(PaymentServiceCollection.Name)]
public class HealthTests(PaymentServiceFactory factory)
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
