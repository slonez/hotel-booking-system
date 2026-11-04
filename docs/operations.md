# Operations

How to start, stop and recover the containerized system (`docker-compose.prod.yml`). First-time setup, backups and
dead letters are in the [README](../README.md). Examples use:

```bash
compose="docker compose -f docker-compose.prod.yml --env-file .env.prod"
```

## Start and stop

```bash
$compose up -d --build --wait    # Waits until services with a health check are healthy
$compose ps
$compose down                    # Keeps the data. Add -v only to delete it.
```

Compose starts things in the right order: PostgreSQL and RabbitMQ, then the services, then Caddy. Start `stripe listen`
separately (see the README), otherwise payments stay unconfirmed.

A new version: take a backup, then `up -d --build --wait`. Migrations run when each service starts.
If a service keeps restarting, read its log. A failed migration is a typical cause.

## Finding out what happened

```bash
$compose logs --since 1h booking
$compose logs --since 1h booking | grep <booking id>
```

Logs are JSON. One flow connects through `BookingId` and `MessageId`, not through a shared trace ID.
Dashboards: Grafana, http://localhost:3000. Firing alerts: http://localhost:9093.

## Alerts

| Alert | Meaning | What to do |
|---|---|---|
| `ServiceDown` | Prometheus cannot reach a service | `$compose ps`, read its log, `$compose up -d <service>` |
| `ServiceUnhealthy` | A service has lost PostgreSQL or RabbitMQ | Check `$compose ps postgres rabbitmq`. Services reconnect by themselves. |
| `OutboxStuck` | Events are not being published | RabbitMQ is down, or the consumer of the oldest event has never started and declared its queue. Start it. Messages then go out by themselves. |
| `DeadLetters` | A message could not be handled | Read the consumer's log for `dead lettering`. Fix the cause, then move the messages back (README). |
| `RefundFailed` | Stripe refused a refund 5 times | See below. |
| `WebhooksMissed` | Reconciliation had to fix a payment | Check that webhooks reach Payment Service. Locally: is `stripe listen` running? Bookings are already corrected. |
| `GatewayErrors` | Over 5 % of API requests fail | The Gateway's log shows the failing call. Check Booking and Payment. |

A database or RabbitMQ outage over about 100 seconds moves messages to the dead letter queues, so `DeadLetters` often
follows `ServiceUnhealthy`. After the outage, move them back.

## Failed refund

Customers are waiting for this money.

1. Find the reason in Payment's log: `$compose logs payment | grep <refund id>`.
2. List failed refunds:
   ```bash
   $compose exec postgres psql -U postgres -d payment -c \
     'SELECT r."Id", p."BookingId", p."StripePaymentIntentId", r."AmountCents" FROM "Refunds" r JOIN "Payments" p ON p."Id" = r."PaymentId" WHERE r."Status" = '"'"'Failed'"'"';'
   ```
3. Check the payment in the Stripe dashboard and refund it there if it is still unrefunded.
4. Mark it handled. The alert clears within about two minutes:
   ```bash
   $compose exec postgres psql -U postgres -d payment -c \
     'UPDATE "Refunds" SET "Status" = '"'"'Succeeded'"'"' WHERE "Id" = '"'"'<refund id>'"'"';'
   ```

Do not set it back to `Pending` to retry soon after. Retries use the same idempotency key, and Stripe can answer
with the saved failure.

## Customer paid, booking stays Held

The webhook has not arrived: the payment is `Pending` in Payment Service. Locally the usual cause is that `stripe listen`
is not running.

Wait. `PaymentReconciliationJob` asks Stripe about payments that are over 10 minutes old, every 5 minutes, and treats the
answer like a webhook. The booking is confirmed about 15 minutes after the payment at the latest, or refunded if
no room is left. If it does not happen, look for `Could not check payment` in Payment's log.

## Full recovery

If PostgreSQL's data is lost: `$compose up -d --wait`, then `docker/postgres/restore.sh <timestamp>`, and go through the
list under *After a restore* in the README. RabbitMQ's data needs no backup: services declare their queues again, and
only messages in queues at the moment of the loss are gone. Compare recent payments with Stripe in that case too.

## Before deploying publicly

Not done, because Phase 7 runs locally:

- Replace `localhost` in the Caddyfile with a domain, which also gives a real certificate. Change the Stripe URLs in compose.
- Register a Stripe webhook endpoint, pinned to the Stripe.net API version. Otherwise events are rejected.
- Use a real SMTP provider with TLS required, and send alerts somewhere other than Mailpit.
- Start Notification Service before a Booking version that publishes a new event.
- Copy backups off the machine. Alert when a backup fails.
- Build images in CI and deploy them, instead of `--build` on the server.
