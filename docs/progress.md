# Progress

## Current phase

Phase 7 — Production Readiness

## Next step

Phase 7 step 3: health checks and structured logs. Step 5 replaces the fixed database and RabbitMQ passwords in `docker-compose.prod.yml`.

## Confirmed decisions

- Project lives in `~/repos/hotel-booking-system`.
- MVP users: customers with login. No admin UI in MVP (hotels and rooms come from seed data).
- Multi-hotel platform.
- Customer books a room type, not a specific room.
- Full payment upfront at booking time.
- Room is held for the customer during payment (temporary reservation).
- Hold lasts 30 minutes (matches Stripe Checkout minimum session lifetime). If not paid by then, the room is released.
- Late payment: if a room of the same type is still available, confirm the booking. Otherwise notify and fully refund.
- Cancellation and refund are in MVP scope. 100% refund if cancelled at least 24 h before check-in, 50% after that.
- Search works without login. Booking requires login.
- Rate limiting instead of DDoS protection (NFR-7).
- Services: API Gateway, Booking Service, Payment Service, Notification Service.
- Data ownership:
  - Booking Service: hotels, room types, prices, bookings (incl. holds and statuses), availability. Search lives here. Stores customer email from JWT on the booking.
  - Payment Service: payments, refunds.
  - Notification Service: log of sent notifications only. Receives all needed data in events.
  - API Gateway: stateless, no database. JWT validation, rate limiting (in memory), routing.
- Communication:
  - Search: Frontend → Gateway → Booking (sync).
  - Start booking: Gateway → Booking (creates hold) → Payment (creates Stripe Checkout session), sync. Booking orchestrates, Gateway stays thin.
  - If Payment call fails: hold is kept, customer sees error and retries. Retry uses booking ID as idempotency key.
  - Payment succeeded: Stripe webhook → Payment → `PaymentSucceeded` event → Booking (async).
  - Confirmation email: Booking → event → Notification (async).
  - Cancellation: sync in Booking (status + room released), refund async via refund event → Payment.
  - Hold expiry: background job inside Booking, no messaging.
- One PostgreSQL server, one database + DB user per service.
- Stripe webhooks go directly to Payment Service, not through the Gateway.
- Booking decides refunds and calculates the amount; Payment only executes. Same refund event for cancellation and late payment.
- Microservices chosen for learning (ADR-001).
- Reliable publishing: transactional outbox in Booking and Payment (at-least-once delivery).
- Idempotent consumers:
  - Booking: state-based, option B (`PaymentSucceeded` moves Held or Expired → Confirmed if a room is free; already Confirmed → ignore). Check + update must be atomic.
  - Notification: log of sent notifications (unique `BookingId` + type).
  - Payment (Stripe webhooks): state-based, `Pending` → `Succeeded`/`Expired` only (no processed-events table; decided in Phase 5).
- Booking states: Held, Confirmed, Expired, Cancelled (see `docs/data-and-api-design.md`). Late payment without room stays `Expired` with `RefundAmountCents` set, which also marks duplicate messages.
- Booking schema: Hotels, RoomTypes (TotalRooms), Bookings (TotalPrice, ExpiresAt).
- Double booking prevented with `SELECT ... FOR UPDATE` on the room type row. `ExpiresAt` is the source of truth for hold expiry.
- Payment states: Pending, Succeeded, Expired. Refund states: Pending, Succeeded, Failed (manual).
- Payment schema: Payments (BookingId unique), Refunds (PaymentId unique, own table), OutboxMessages.
- Events: `PaymentSucceeded`, `BookingConfirmed`, `RefundRequested` (see `docs/data-and-api-design.md`).
- gRPC/REST contracts are defined in Phase 4 per service, not upfront.
- Repo: monorepo, one solution (`HotelBooking.slnx`). One project per service, no layered architecture. Protos in `src/Contracts/Protos`, event records in `src/Contracts/Events` (both linked into services).
- Shared library `src/Messaging` (`HotelBooking.Messaging`) for technical messaging code only, no domain logic: `OutboxMessage` + `ConfigureOutbox()`, `OutboxPublisher<TContext>`, `EventConsumer<TEvent, THandler>` with `IEventHandler<TEvent>`. Registered with `AddOutboxPublisher<TContext>()` and `AddEventConsumer<TEvent, THandler>(queue, routingKey)`.
- .NET 10, PostgreSQL 18 in Docker Compose (host port 5433). Migrations applied at startup in Development.
- Money stored as integer cents (EUR).
- User identity (Clerk user ID, email) is passed in gRPC requests by the Gateway. Booking trusts the Gateway (internal network).
- gRPC errors: unknown room type → `NotFound`, no rooms → `FailedPrecondition`, invalid input → `InvalidArgument`.
- Gateway: Minimal API, REST → gRPC via `AddGrpcClient`. gRPC errors mapped to ProblemDetails in `GrpcExceptionHandler`. Gateway tests use a fake Bookings client.
- Gateway auth: JWT bearer with Clerk as authority, no audience validation, `MapInboundClaims = false`. Booking endpoint requires `email` claim.
- Rate limiting: global fixed window per client IP (default 100/min).
- Frontend: Vite + React + TypeScript, `@clerk/react` v6. Vite dev server proxies `/api` to the Gateway (no CORS in development). Single page: search, sign in, book.
- Hold expiry: `HoldExpiryJob` (BackgroundService), runs every minute, one atomic `ExecuteUpdate` (`Held` + `ExpiresAt <= now` → `Expired`). Errors are logged; next tick retries.
- Tests: WebApplicationFactory + Testcontainers (real PostgreSQL), `FakeTimeProvider` for time. Booking tests share one container (xUnit collection fixture) and clear bookings before each test.
- Gateway → Booking gRPC calls get a 5 s default deadline (`DeadlineInterceptor`). `DeadlineExceeded` → 504.
- Stripe Checkout (hosted page), not Payment Intents.
- RabbitMQ + transactional outbox are built in Phase 5 for `PaymentSucceeded` (no temporary gRPC call). Phase 6 adds Notification.
- Payment start: two calls. `POST /api/bookings` creates the hold; `POST /api/bookings/{id}/checkout` → Booking `StartPayment` → Payment `CreateCheckoutSession` returns the Stripe URL. Retry = same call again.
- Checkout session expires 30 min after creation (Stripe minimum), so it can outlive the hold; paying after hold expiry is a late payment.
- Checkout idempotency via unique `Payments.BookingId`, not a Stripe idempotency key (parameters differ per call). Extra sessions from races are never shown and expire unpaid.
- Payment Service: gRPC on port 5242, `payment` database. Stripe secret key in user secrets. Tests replace Stripe's `SessionService` with a fake.
- Booking → Payment gRPC uses `EnableCallContextPropagation()`: the Gateway's deadline (15 s for checkout) and cancellation flow through. Payment errors pass through Booking unchanged. Stripe errors → `Unavailable` (generic message, details logged).
- Frontend: after booking, redirects to Stripe Checkout. Held bookings have a Pay button. Stripe returns to `/?payment=success|cancelled`; booking stays Held until the webhook flow (steps 2–3).
- Stripe webhook: `POST /stripe/webhook` on a separate HTTP/1.1 port (5243); gRPC stays HTTP/2 on 5242. Signature checked with `EventUtility.ConstructEvent`; secret in user secrets. Local testing with `stripe listen`.
- Messaging: `RabbitMQ.Client` 7 + own small outbox (not MassTransit), to see how at-least-once works. RabbitMQ 4 in Docker Compose.
- Outbox (Payment): `OutboxMessages` saved in the webhook transaction only when the payment actually changed to Succeeded. `OutboxPublisher` publishes every second as mandatory with publisher confirms, marks each message after its confirm. Without a bound queue the message stays in the outbox.
- Booking consumer: `EventConsumer<PaymentSucceeded, PaymentSucceededHandler>` (RabbitMQ, durable queue `booking.payment-succeeded`, prefetch 1, ack after handling). Handler logic is tested directly. Late payment re-checks availability under the room type lock, using the time read after the lock.
- Consumer errors: quorum queues with a dead letter queue `<queue>.dead`. `EventConsumer` makes 20 attempts 5 s apart (about 100 s) while holding the message, then dead letters it; malformed messages go there at once. Own counting because `x-delivery-limit` does not count nacks with requeue. Dead letters are moved back by hand.
- Notification Service: web host without endpoints on port 5252 (same setup and test factory as other services; health checks in Phase 7), `notification` database. Log rows hold no customer data.
- Notifications (Phase 6): MailKit + Mailpit in compose. Duplicates stopped with unique `(BookingId, Type)` instead of message ID; email is sent first and then logged, so a crash can duplicate an email but not lose it.
- Refunds: `RefundRequested` consumer only saves a Pending `Refunds` row (unique `PaymentId` stops duplicates). A separate `RefundProcessor` job calls Stripe.
- Background loops and consumers treat an exception as shutdown only when their own token is cancelled, so a timeout is logged and retried.
- Phase 7 runs locally, without a VPS: little new to learn compared to its cost. Caddy with a local certificate, Mailpit for email, `stripe listen` for webhooks. JSON console logs (built in), `prometheus-net`, migrations at startup behind a setting.
- Containers: `docker-compose.prod.yml` (own project and volumes, runs alongside dev), one `docker/service.Dockerfile` with a `PROJECT` build arg, services run as non-root. Caddy serves the frontend on HTTPS (local CA) and routes `/api/*` to Gateway and `/stripe/webhook` to Payment. Only Caddy publishes ports to all interfaces; admin UIs on 127.0.0.1.
- `Database:MigrateOnStartup` (default: on in Development). `ForwardedHeaders:Enabled` on the Gateway only behind the proxy; the header is trusted from any address, so the Gateway must not be reachable otherwise.
- CI: GitHub Actions runs backend tests and frontend lint and build on pushes and pull requests.
- Collaboration style: Claude recommends, user decides; fewer exercises, more building (project brief updated).
- Authentication with Clerk (hosted, ADR-002). Services store only the Clerk user ID. Gateway validates JWT; ownership checks (NFR-6) happen in services.

## Open questions

### Core Implementation

- None.

### Later

- Accepted in Phase 6 review: on the very first deploy, `RefundRequested` published before Notification has declared its queue reaches Payment only, so that email is lost (mandatory passes because Payment's queue is bound). Emails from different queues can arrive out of order.
- Phase 6 or later: the customer sees the requested refund amount, not whether the refund succeeded.
- Phase 7: alert on `Failed` refunds and non-empty dead letter queues.
- If deployed publicly later (VPS, domain, automatic deploy): start Notification Service before a Booking version that publishes a new event, otherwise Booking's outbox stops at it. Real SMTP provider with TLS required. Pin the production webhook endpoint's API version to the SDK's, otherwise events are rejected.

## Log

- 2026-10-28: Project created. Phase 1 started.
- 2026-10-28: First MVP decisions made (users, multi-hotel, room type, payment upfront, hold).
- 2026-10-28: Hold time, late payment, cancellation, search/login decided.
- 2026-10-28: MVP decisions complete. Draft requirements written.
- 2026-10-28: FRs approved. First NFR draft (NFR-1..7) written, under review.
- 2026-10-28: NFRs refined (NFR-1..12). XSS and SQL injection split.
- 2026-10-28: NFR-13 (Stripe webhook signature verification) added.
- 2026-10-28: Edge cases written and reviewed.
- 2026-10-28: Phase 1 complete (`docs/requirements.md`). Phase 2 started.
- 2026-10-28: Service boundaries proposed. Booking owns catalog + availability. Clerk chosen for auth.
- 2026-10-28: Data ownership decided. CSRF resolved (Bearer token in header).
- 2026-10-28: Communication patterns decided.
- 2026-10-28: Transactional outbox and idempotent consumers decided.
- 2026-10-28: Draft `docs/architecture.md` written.
- 2026-10-28: DB layout, webhook route and late payment flow decided.
- 2026-10-28: ADR-001 written and accepted.
- 2026-10-29: ADR-002 (auth provider) chosen as next ADR.
- 2026-10-29: ADR-002 accepted. NFR-10 updated: no passwords stored.
- 2026-10-29: Phase 2 complete (`docs/architecture.md`, ADR-001, ADR-002). Phase 3 started.
- 2026-10-29: Booking states decided.
- 2026-10-29: Booking schema and concurrency control decided.
- 2026-10-30: Payment states, schema and events decided. Phase 3 complete. Phase 4 started.
- 2026-10-30: Project brief mentoring style updated (recommend, then decide).
- 2026-10-30: Step 1 done: Booking Service with schema, seed data, `SearchAvailability` gRPC, 9 integration tests passing.
- 2026-10-30: Step 2 done: `CreateBooking` with row lock. Concurrency test (10 parallel requests, 1 room) passes; without the lock it fails.
- 2026-10-30: Step 3a done: Gateway with `GET /api/search` and gRPC error mapping. Verified end to end (curl → Gateway → Booking → PostgreSQL).
- 2026-10-30: Step 3b done: Clerk JWT auth, `POST /api/bookings`, rate limiting. 25 tests passing.
- 2026-10-30: Step 4: React frontend (search, Clerk sign-in, booking). Verified in the browser with real Clerk sign-in.
- 2026-10-30: Hold expiry job done. 26 tests passing; verified against the running service.
- 2026-10-30: My bookings (FR-5) done: `ListBookings` gRPC, `GET /api/bookings`, frontend list. 30 tests passing.
- 2026-10-30: Cancellation (FR-14..16) done: `CancellationPolicy`, `CancelBooking` gRPC, `POST /api/bookings/{id}/cancel`, frontend button. 46 tests passing. Phase 4 features complete.
- 2026-10-30: Phase 4 review fixes: gRPC deadline (504), shared test container and test data helpers, idempotency note corrected. 49 tests passing. Phase 4 complete.
- 2026-10-30: Phase 5 started.
- 2026-10-31: Phase 5 decisions: Stripe Checkout, RabbitMQ + outbox in Phase 5.
- 2026-10-31: Phase 5 step 1a done: Payment Service with `CreateCheckoutSession` (Stripe Checkout, idempotent per booking). 7 tests passing; concurrency test fails without the unique-violation handling.
- 2026-10-31: Phase 5 step 1b done: `StartPayment`, `POST /api/bookings/{id}/checkout`, Stripe redirect and Pay button. 66 tests passing; verified against real Stripe test mode with grpcurl.
- 2026-10-31: Webhook idempotency changed to state-based (no `ProcessedStripeEvents` table): the atomic `WHERE Status = 'Pending'` update already ignores duplicates.
- 2026-10-31: Phase 5 step 2 done: Stripe webhook (signature check, completed/expired → Succeeded/Expired). 74 tests passing.
- 2026-10-31: Phase 5 step 3a done: RabbitMQ in compose, transactional outbox and `OutboxPublisher` in Payment, `PaymentSucceeded` event. 78 tests passing (publisher tested against real RabbitMQ).
- 2026-10-31: Phase 5 step 3b done: Booking consumes `PaymentSucceeded` and confirms bookings (incl. late payment when a room is free). 86 tests passing, consumer tested through real RabbitMQ.
- 2026-10-31: Phase 5 step 4 plan: shared `src/Messaging` library, refunds via background job.
- 2026-10-31: Phase 5 step 4a done: outbox publisher and consumer moved to `src/Messaging`; no behavior or schema change. 86 tests passing.
- 2026-10-31: Phase 5 step 4b done: Booking outbox; cancellation and late payment without room publish `RefundRequested` (reason `Cancelled`/`LatePayment`). 86 tests passing.
- 2026-10-31: Phase 5 step 4c done: Payment consumes `RefundRequested` into `Refunds`, `RefundProcessor` refunds through Stripe (idempotency key = refund ID, 5 attempts → Failed); webhook stores `StripePaymentIntentId`. 93 tests passing.
- 2026-11-01: Phase 5 review fixes: duplicate message cannot confirm a refunded booking, hold expiry checked after the lock (overbooking race), mandatory publishing, timeouts no longer stop background work, webhook signature header → 400, refund wording. 98 tests passing. Phase 5 complete.
- 2026-11-02: Phase 6 started.
- 2026-11-02: Phase 6 step 1 done: dead letter queues and retry limit in `src/Messaging`, tested through real RabbitMQ in new `Messaging.Tests`.
- 2026-11-02: Phase 6 step 2 done: Booking publishes `BookingConfirmed` through the outbox when a booking is confirmed.
- 2026-11-02: Phase 6 step 3 done: Notification Service sends confirmation, cancellation and late-payment emails (tested through real RabbitMQ, PostgreSQL and Mailpit). 111 tests passing.
- 2026-11-02: Phase 6 step 4 done: recovery tests through real RabbitMQ and PostgreSQL (crash during handling, RabbitMQ restart, start during outage, dead letter move with a shovel). Dead letter procedure in README.
- 2026-11-02: Phase 6 reviewed, remaining items accepted under "Later". 116 tests passing. Phase 6 complete.
- 2026-11-02: Phase 7 started.
- 2026-11-02: Phase 7 scope changed: no VPS for now. Step 1: CI workflow added.
- 2026-11-02: First CI run failed on an intermittent cleanup error: the host called `StopAsync` several times at once and closed a RabbitMQ connection in use. Consumer and publisher now close once.
- 2026-11-02: Phase 7 step 2 done: whole system in containers behind Caddy, smoke tested (search, webhook route, message from RabbitMQ to Mailpit); user verified the flow in the browser with real Clerk and Stripe test mode.
