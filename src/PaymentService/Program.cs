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
// The secret key comes from user secrets in development, never from appsettings.
builder.Services.AddSingleton<IStripeClient>(_ => new StripeClient(builder.Configuration["Stripe:SecretKey"]));
builder.Services.AddSingleton(sp => new SessionService(sp.GetRequiredService<IStripeClient>()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using (var scope = app.Services.CreateScope())
        scope.ServiceProvider.GetRequiredService<PaymentDbContext>().Database.Migrate();

    app.MapGrpcReflectionService();
}

app.MapGrpcService<PaymentsService>();

app.Run();
