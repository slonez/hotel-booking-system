using RabbitMQ.Client;

namespace HotelBooking.Messaging;

// The queues of one consumer: a work queue bound to the events exchange and a dead letter queue
// for messages that cannot be handled. Moving dead letters back to the work queue is a manual step
// and safe, because handlers are idempotent.
public static class ConsumerQueue
{
    // Handling attempts before EventConsumer dead letters a message. With the default retry delay
    // this rides out an outage of about 100 seconds; after that messages wait in the dead letter queue.
    public const int MaxAttempts = 20;

    public static string DeadLetterName(string queue) => $"{queue}.dead";

    public static async Task DeclareAsync(IChannel channel, string queue, string routingKey, CancellationToken ct = default)
    {
        await channel.ExchangeDeclareAsync(Exchanges.Events, ExchangeType.Topic, durable: true, cancellationToken: ct);

        var deadLetterQueue = DeadLetterName(queue);
        await channel.QueueDeclareAsync(deadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: ct);

        // The default exchange routes by queue name. Quorum queues support at-least-once dead lettering, which
        // does not lose a message, such as a payment, on the way to the dead letter queue. It requires
        // reject-publish overflow. x-delivery-limit is not used: it does not count nacks with requeue.
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = deadLetterQueue,
                ["x-dead-letter-strategy"] = "at-least-once",
                ["x-overflow"] = "reject-publish"
            }, cancellationToken: ct);
        await channel.QueueBindAsync(queue, Exchanges.Events, routingKey, cancellationToken: ct);
    }
}
