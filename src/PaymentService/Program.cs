using HotelBooking.Contracts.Events;
using HotelBooking.Messaging;
using Microsoft.EntityFrameworkCore;
using PaymentService;
using PaymentService.Data;
using Stripe;
using Stripe.Checkout;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddGrpcReflection();
builder.Services.AddDbContext<PaymentDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("PaymentDb")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOutboxPublisher<PaymentDbContext>();
builder.Services.AddEventConsumer<RefundRequested, RefundRequestedHandler>(
    RefundRequestedHandler.Queue, RefundRequested.RoutingKey);
builder.Services.AddSingleton<RefundProcessor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RefundProcessor>());
// The secret key comes from user secrets in development, never from appsettings.
builder.Services.AddSingleton<IStripeClient>(_ => new StripeClient(builder.Configuration["Stripe:SecretKey"]));
builder.Services.AddSingleton(sp => new SessionService(sp.GetRequiredService<IStripeClient>()));
builder.Services.AddSingleton(sp => new RefundService(sp.GetRequiredService<IStripeClient>()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using (var scope = app.Services.CreateScope())
        scope.ServiceProvider.GetRequiredService<PaymentDbContext>().Database.Migrate();

    app.MapGrpcReflectionService();
}

app.MapGrpcService<PaymentsService>();
app.MapPost("/stripe/webhook", StripeWebhook.HandleAsync);

app.Run();
