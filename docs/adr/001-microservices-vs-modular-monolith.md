# ADR-001: Microservices instead of modular monolith

Status: Accepted

## Context

We need to decide whether to build the system as microservices or as a modular monolith.

The system is small: one team (one developer), four services, low expected load (NFR-5), and everything runs on a single VPS. From a pure engineering point of view, a modular monolith would be enough.

## Decision

We build the system as microservices.

The main reason is learning. I have built monoliths before and want to understand microservices in practice: service boundaries, data ownership, and communication between services.

## Consequences

### Benefits

- Some failure isolation: if one service is down, others keep working. For example, search works when Payment Service is down.
  - Limited in practice: all services run on the same VPS and the same PostgreSQL server. If either goes down, everything goes down.

### Costs

More complexity compared to a modular monolith:

- In a monolith, payment confirmation and booking confirmation would be one database transaction.
- No need for a transactional outbox or idempotent consumers between services, and no state where the payment succeeded but the booking is not yet confirmed. (Idempotent Stripe webhook handling would still be needed.)
- No need for gRPC contracts, RabbitMQ, or four separate deployments.
- Debugging would be easier, because one request would not travel through several services.

Many of the problems solved in Phase 2 exist only because of this decision.
