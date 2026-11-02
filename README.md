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
docker compose up -d                        # PostgreSQL on 5433, RabbitMQ on 5672 (UI: http://localhost:15672, guest/guest), Mailpit (UI: http://localhost:8025)
dotnet run --project src/NotificationService  # Consumes events, sends emails to Mailpit. Start before the first booking is confirmed.
dotnet run --project src/BookingService     # gRPC on :5232, health check on http://localhost:5233/health
dotnet run --project src/PaymentService     # gRPC on :5242, Stripe webhooks on :5243
stripe listen --events checkout.session.completed,checkout.session.expired,checkout.session.async_payment_succeeded \
  --forward-to localhost:5243/stripe/webhook
dotnet run --project src/Gateway            # REST on http://localhost:5172
cd frontend && npm run dev                  # http://localhost:5173
```

## Run in containers

The whole system in a production-like setup: Caddy on HTTPS is the only way in. It runs alongside the setup above,
but needs ports 80 and 443.

1. `cp .env.prod.example .env.prod`, `chmod 600 .env.prod` and fill in the values.
2. Start:
   ```bash
   docker compose -f docker-compose.prod.yml --env-file .env.prod up -d --build
   stripe listen --events checkout.session.completed,checkout.session.expired,checkout.session.async_payment_succeeded \
     --forward-to https://localhost/stripe/webhook --skip-verify
   ```
3. Open https://localhost and accept the browser's warning about Caddy's local certificate.
   Emails: http://localhost:8026. RabbitMQ: http://localhost:15673 (user `hotel`).

Monitoring: Grafana http://localhost:3000 (user `admin`), Prometheus http://localhost:9090, Alertmanager http://localhost:9093.
Alerts arrive as emails in Mailpit.

Alerts, failed refunds and other recovery steps: [`docs/operations.md`](docs/operations.md).

Stop with `docker compose -f docker-compose.prod.yml --env-file .env.prod down`. Add `-v` to delete the data.

PostgreSQL and RabbitMQ read their passwords only when their volumes are first created. To change one on existing
data, change it inside the service too (`ALTER USER`, `rabbitmqctl change_password`).

## Backup and restore

The `backup` container dumps each database to `backups/<timestamp>/` at every start and once a day, and removes
backups older than 7 days. The folder survives `down -v`, but it is on this machine only: copy it elsewhere for
real protection. Backup failures appear only in `docker compose logs backup`.

Take a backup by hand before anything risky, such as a new version with migrations. Migrations run forward only:
the way back from a bad one is a restore.

```bash
docker compose -f docker-compose.prod.yml --env-file .env.prod exec backup /backup.sh
docker/postgres/restore.sh <timestamp>
```

After a restore:

- Changes made after the backup are gone, also in Stripe's direction: a payment made after it is known to Stripe but
  not to us. Compare recent payments in the Stripe dashboard. Stripe can resend their events.
- Booking and Payment are dumped one after the other, so they can differ by a moment. An unpublished outbox
  message is sent again, which is safe because consumers are idempotent.
- Emails in a restored state can go out again, because the log of sent emails is also restored.

## Dead letters

A message that cannot be handled ends up in `<queue>.dead`. Fix the cause, then move the messages back.
Handlers are idempotent, so this is safe. The shovel removes itself when the queue is empty.

With the containerized setup, add `-f docker-compose.prod.yml --env-file .env.prod` after `docker compose`.

```bash
docker compose exec rabbitmq rabbitmq-plugins enable rabbitmq_shovel
docker compose exec rabbitmq rabbitmqctl set_parameter shovel replay-dead-letters \
  '{"src-protocol":"amqp091","src-uri":"amqp://","src-queue":"booking.payment-succeeded.dead","src-delete-after":"queue-length",
    "dest-protocol":"amqp091","dest-uri":"amqp://","dest-queue":"booking.payment-succeeded"}'
```

## Tests

```bash
dotnet test    # requires Docker (Testcontainers)
```
