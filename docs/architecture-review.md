# Architecture review

Status: draft for discussion, 2026-11-04. The learning retrospective is written last, after the review.

## Verdict

On engineering grounds alone, a modular monolith is the better fit for these requirements: one developer, low load,
one machine. Microservices are justified here only as a learning choice, which is what ADR-001 says. That decision
held up: the measurements below show little practical benefit from the split, and the costs were real.

The code difference is smaller than expected. The larger cost is the extra failure modes and operations.

## Measurements

Taken on one development machine, everything in Docker, on commit `6fce80a`.

### Load (NFR-5: search p95 under 500 ms at 50 requests/s)

| Bookings in the database | p95 | Max | Failed |
|---|---|---|---|
| Seed data only | 5.7 ms | 507 ms | 0 / 3000 |
| 50 000 (all `Cancelled`) | 11.4 ms | 18 ms | 0 / 3001 |

NFR-5 is met with a wide margin, through Caddy with the rate limit raised. This is not proof of production capacity:
the client runs on the same machine, it repeats one search, and there are no concurrent writes. Cancelled rows do not
count towards availability, so a database full of active bookings could behave differently.

### Failure isolation

| Stopped | Search | Time |
|---|---|---|
| Payment | Works | 167 ms |
| Notification | Works | 240 ms |
| RabbitMQ | Works | 117 ms |
| Booking | Fails | 5169 ms |
| PostgreSQL | Fails | 141 ms |

NFR-4 holds. But a monolith would pass the same test for Stripe outages, because search does not call Stripe. The split
adds isolation only against a crash of one service's process. PostgreSQL and the machine remain shared.

Finding: with Booking down, search waits for the whole 5 s deadline instead of failing at once.

### Memory when idle

| Part | Memory |
|---|---|
| Four .NET services | 285 MiB |
| RabbitMQ | 127 MiB |
| Grafana | 487 MiB |
| Everything else | 262 MiB |
| Total | 1161 MiB |

Measured before load. The services and RabbitMQ are about a third of the total. Monitoring costs more than the split.

### Size

| | C# lines, without migrations |
|---|---|
| Gateway | 230 |
| Booking | 555 |
| Payment | 444 |
| Notification | 194 |
| Messaging | 372 |
| Contracts | 46 (plus 115 lines of proto) |
| Tests | 2868 |

8 of the 24 commits that changed `src` changed two or more services. That is partly because features were built across
them on purpose, but a third of the changes crossing boundaries does not suggest independent services.

## Service boundaries

| Boundary | Assessment |
|---|---|
| **Payment** | The strongest. Stripe keys, webhooks and refunds sit in one place, with a small and stable interface: one gRPC call and two events. It would be a clear module in a monolith too. |
| **Notification** | The cleanest. It only consumes events, stores no customer data and can be down without affecting anyone. It is also so small that the split gains little. |
| **Gateway** | The weakest. Five REST endpoints map almost one to one to gRPC methods, all on Booking. Only JWT validation and rate limiting are its own work, and either could live in Booking or Caddy. |
| **Booking** | Correctly the core. Keeping catalog and availability together made the `FOR UPDATE` lock possible. A separate Room Service would have turned the most important invariant (NFR-1) into a distributed problem. |

Dropping the Room Service candidate was the best boundary decision in the project.

## Modular monolith comparison

| | This system | Modular monolith |
|---|---|---|
| Payment succeeded → booking confirmed | Eventually, through outbox and RabbitMQ | One transaction |
| Deployable .NET units | 4 | 1 |
| Containers needed by the application itself | 8 | 4 (app, PostgreSQL, Caddy, Mailpit) |
| Contracts between parts | Proto files and event records | C# interfaces, checked by the compiler |
| Following one flow | Across logs, through `BookingId` | One log and stack trace |
| Scaling one part alone | Possible in principle | Not possible |
| Deploying one part alone | Possible, but with ordering rules | Not possible |

**What stays in a monolith.** ADR-001 slightly overstates what goes away. These problems come from Stripe and SMTP, not
from the split:

- Webhook signatures, duplicates and ordering.
- Late payments and refunds.
- Retrying refunds and emails. SMTP and Stripe cannot join a database transaction, so a job table much like the outbox
  is still needed, but without RabbitMQ.

**What goes away.** Estimated, not measured: about 500 of the roughly 1950 lines, meaning RabbitMQ handling, gRPC and the
mapping between them. Also the states where a payment has succeeded but the booking does not know yet, dead letters
for internal messages, deploy order and service-to-service trust.

### Bugs found in phases 5–7

| Bug | Caused by the split? |
|---|---|
| Duplicate message could confirm a refunded booking | Yes |
| Message lost when no queue was bound | Yes |
| `x-delivery-limit` did not count requeues, so no retry limit | Yes |
| RabbitMQ connection closed during concurrent `StopAsync` | Yes |
| Overbooking when a hold expired during the lock wait | No |
| Timeouts stopped background loops | No |
| Missing webhook signature header gave 500 | No |
| `GatewayErrors` alert could never fire | No |

Four of the eight, all in messaging. The payment-correctness bug among them would not exist with one transaction.

## Unnecessary complexity

Judged against what this system needs, not its learning goals.

| Item | Assessment | If this were production |
|---|---|---|
| gRPC | Learning. Six internal methods with one caller each. | Plain HTTP, or nothing in a monolith |
| Own outbox and consumer | Learning, and it paid off: four real bugs taught how delivery works. | A maintained library |
| Quorum queues | Over-engineered. On one node they replicate nothing; they are used only for at-least-once dead lettering. | Reasonable to keep only with a cluster |
| Separate Gateway | Weakest service, see above. | Merge it into Booking |
| Prometheus, Alertmanager, Grafana | Larger than the application, but justified: alerts found by rules beat problems found by customers. | Keep, or use a hosted service |
| Dead letter by hand with a shovel plugin | Reasonable. Failures should be rare and worth looking at. | Keep |
| 20 attempts while holding the message | Simple, but one bad message stops its queue for 100 s. Fine at this load. | Keep until it hurts |

Things that looked excessive but earned their place: the transactional outbox (given the split, losing a payment event
is unacceptable), idempotent handlers and the recovery tests.

## Scalability

The services assume one instance each. In order of what would need attention first:

| Limit | What happens with more instances or load |
|---|---|
| Rate limit in Gateway memory | Each instance counts separately, so the real limit multiplies |
| Migrations at startup | Instances could race. Needs a separate step. |
| `OutboxPublisher` | Instances publish the same rows. Safe but wasteful; needs `FOR UPDATE SKIP LOCKED`. |
| `RefundProcessor` | Same rows processed twice. The Stripe idempotency key prevents a double refund, but `Attempts` can be wrong. |
| Notification | Check, send, then log: two instances can send the same email twice |
| Room type lock | Serializes bookings per room type. Unmeasured; hot only if one room type gets many simultaneous bookings. |
| One PostgreSQL | The real limit, and a shared point of failure |

None of this is a problem at the required load, so none should be built in advance.

## Gaps and improvements

**Done:** reconciliation with Stripe. A lost webhook was the largest remaining risk to NFR-3: the customer had paid, but the
booking was never confirmed and nothing noticed. `PaymentReconciliationJob` now asks Stripe about old `Pending` payments.

**Knowingly left undone**, in order of importance:

1. **Failed refunds are invisible to the customer.** The email says a refund is on its way even if it later fails.
2. **Services trust the network.** Booking accepts any user ID from whoever reaches its gRPC port. This is fine on one
   Docker network, not across a shared one.
3. **Slow failure when Booking is down.** Search waits 5 s, and the reason is not known.
4. **No alert on failed backups**, and backups stay on one machine.
5. **The token's `azp` claim is not checked.** Audience validation is off because Clerk tokens have none, so the
   authorized party is the check that remains. Compare with Clerk's guidance.
6. **Customer email is kept indefinitely**, in bookings and in backups. No retention rule exists. This would matter for GDPR.
7. **Trace IDs stop at RabbitMQ.**

## Learning retrospective

To be written together.
