using BookingService;
using BookingService.Data;
using HotelBooking.Contracts.Events;
using HotelBooking.Contracts.Payment;
using HotelBooking.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddGrpcReflection();
builder.Services.AddDbContext<BookingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("BookingDb")));
builder.Services.AddGrpcClient<Payments.PaymentsClient>(options =>
    options.Address = new Uri(builder.Configuration["Services:Payment"]!))
    // Calls made while handling a request inherit its deadline and cancellation.
    .EnableCallContextPropagation();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<HoldExpiryJob>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HoldExpiryJob>());
builder.Services.AddOutboxPublisher<BookingDbContext>();
builder.Services.AddEventConsumer<PaymentSucceeded, PaymentSucceededHandler>(
    PaymentSucceededHandler.Queue, PaymentSucceeded.RoutingKey);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using (var scope = app.Services.CreateScope())
        scope.ServiceProvider.GetRequiredService<BookingDbContext>().Database.Migrate();

    app.MapGrpcReflectionService();
}

app.MapGrpcService<BookingsService>();

app.Run();
