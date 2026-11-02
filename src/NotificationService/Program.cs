using HotelBooking.Contracts.Events;
using HotelBooking.Messaging;
using Microsoft.EntityFrameworkCore;
using NotificationService;
using NotificationService.Data;

// A web host for its health check, like the other services.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<NotificationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("NotificationDb")));
builder.Services.AddHealthChecks().AddDbContextCheck<NotificationDbContext>();
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

app.MapHealthChecks("/health");
app.Run();
