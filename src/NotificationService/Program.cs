using HotelBooking.Contracts.Events;
using HotelBooking.Messaging;
using Microsoft.EntityFrameworkCore;
using NotificationService;
using NotificationService.Data;

// A web host without endpoints for now, like the other services. Health checks come in Phase 7.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<NotificationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("NotificationDb")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EmailSender>();
builder.Services.AddScoped<NotificationSender>();
builder.Services.AddEventConsumer<BookingConfirmed, BookingConfirmedHandler>(
    BookingConfirmedHandler.Queue, BookingConfirmed.RoutingKey);
builder.Services.AddEventConsumer<RefundRequested, RefundRequestedHandler>(
    RefundRequestedHandler.Queue, RefundRequested.RoutingKey);

var app = builder.Build();

// One instance per service, so concurrent migrations are not a concern.
if (app.Configuration.GetValue("Database:MigrateOnStartup", app.Environment.IsDevelopment()))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<NotificationDbContext>().Database.Migrate();
}

app.Run();
