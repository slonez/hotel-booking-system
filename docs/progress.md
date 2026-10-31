# Progress

## Current phase

Phase 5 — Payment Integration

## Next step

Phase 5 step 3: transactional outbox in Payment + RabbitMQ, `PaymentSucceeded` consumer in Booking (Held/Expired → Confirmed).

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
  - Notification: log of sent notifications (by message ID).
  - Payment (Stripe webhooks): state-based, `Pending` → `Succeeded`/`Expired` only (no processed-events table; decided in Phase 5).
- Booking states: Held, Confirmed, Expired, Cancelled (see `docs/data-and-api-design.md`). Late payment without room keeps `Expired`.
- Booking schema: Hotels, RoomTypes (TotalRooms), Bookings (TotalPrice, ExpiresAt).
- Double booking prevented with `SELECT ... FOR UPDATE` on the room type row. `ExpiresAt` is the source of truth for hold expiry.
- Payment states: Pending, Succeeded, Expired. Refund states: Pending, Succeeded, Failed (manual).
- Payment schema: Payments (BookingId unique), Refunds (PaymentId unique, own table), OutboxMessages.
- Events: `PaymentSucceeded`, `BookingConfirmed`, `RefundRequested` (see `docs/data-and-api-design.md`).
- gRPC/REST contracts are defined in Phase 4 per service, not upfront.
- Repo: monorepo, one solution (`HotelBooking.slnx`). One project per service, no layered architecture. Protos in `src/Contracts/Protos`.
- .NET 10, PostgreSQL 18 in Docker Compose (host port 5433). Migrations applied at startup in Development.
- Money stored as integer cents (EUR).
- User identity (Clerk user ID, email) is passed in gRPC requests by the Gateway. Booking trusts the Gateway (internal network).
- gRPC errors: unknown room type → `NotFound`, no rooms → `FailedPrecondition`, invalid input → `InvalidArgument`.
- Gateway: Minimal API, REST → gRPC via `AddGrpcClient`. gRPC errors mapped to ProblemDetails in `GrpcExceptionHandler`. Gateway tests use a fake Bookings client.
- Gateway auth: JWT bearer with Clerk as authority, no audience validation, `MapInboundClaims = false`. Booking endpoint requires `email` claim.
- Rate limiting: global fixed window per client IP (default 100/min). Forwarded headers needed behind reverse proxy (Phase 7).
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
- Collaboration style: Claude recommends, user decides; fewer exercises, more building (project brief updated).
- Authentication with Clerk (hosted, ADR-002). Services store only the Clerk user ID. Gateway validates JWT; ownership checks (NFR-6) happen in services.

## Open questions

### Core Implementation

- None.

### Later (Phase 3/5)

- Cancellation does not publish `RefundRequested` yet (needs outbox).

- Payment can succeed right before hold expiry while the webhook arrives after it. Late payment handling (FR-12) covers this.

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
