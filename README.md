# Hotel Booking System

Learning project: a microservices hotel booking system. Design docs are in [`docs/`](docs/).

## Run locally

Requirements: .NET 10 SDK, Node 24, Docker.

One-time setup:

1. Create a Clerk application and add the `email` claim to the session token
   (Clerk Dashboard → Sessions → Customize session token):
   ```json
   { "email": "{{user.primary_email_address}}" }
   ```
2. Set `Clerk:Authority` in `src/Gateway/appsettings.json` to the Clerk Frontend API URL.
3. Copy `frontend/.env.example` to `frontend/.env.local` and set the Clerk publishable key.
4. `cd frontend && npm install`
5. Stripe test mode secret key for Payment Service:
   ```bash
   dotnet user-secrets --project src/PaymentService set Stripe:SecretKey sk_test_...
   ```
6. Install the [Stripe CLI](https://docs.stripe.com/stripe-cli) and run `stripe login`.
   Save the CLI's webhook signing secret once (it stays the same for your account):
   ```bash
   dotnet user-secrets --project src/PaymentService set Stripe:WebhookSecret $(stripe listen --print-secret)
   ```

The database for each service is created by `docker/postgres/init.sql` when the volume is first created.
After adding a service, recreate the volume (`docker compose down -v`) or run its `CREATE USER/DATABASE` lines manually.

Start (each in its own terminal):

```bash
docker compose up -d                        # PostgreSQL on 5433, RabbitMQ on 5672 (UI: http://localhost:15672, guest/guest)
dotnet run --project src/BookingService     # gRPC on http://localhost:5232
dotnet run --project src/PaymentService     # gRPC on :5242, Stripe webhooks on :5243
stripe listen --events checkout.session.completed,checkout.session.expired,checkout.session.async_payment_succeeded \
  --forward-to localhost:5243/stripe/webhook
dotnet run --project src/Gateway            # REST on http://localhost:5172
cd frontend && npm run dev                  # http://localhost:5173
```

## Tests

```bash
dotnet test    # requires Docker (Testcontainers)
```
