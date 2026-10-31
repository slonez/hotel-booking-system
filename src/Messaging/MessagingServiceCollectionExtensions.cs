using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HotelBooking.Messaging;

public static class MessagingServiceCollectionExtensions
{
    // Publishes OutboxMessages saved through TContext. Also registered as a singleton so tests can run it directly.
    public static IServiceCollection AddOutboxPublisher<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.AddSingleton<OutboxPublisher<TContext>>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxPublisher<TContext>>());
        return services;
    }

    // Consumes events with the given routing key from the service's own durable queue.
    public static IServiceCollection AddEventConsumer<TEvent, THandler>(
        this IServiceCollection services, string queue, string routingKey)
        where THandler : class, IEventHandler<TEvent>
    {
        services.AddScoped<THandler>();
        services.AddHostedService(sp =>
            ActivatorUtilities.CreateInstance<EventConsumer<TEvent, THandler>>(sp, queue, routingKey));
        return services;
    }
}
