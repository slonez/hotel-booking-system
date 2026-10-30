# ADR-002: Authentication provider

Status: Accepted

## Context

We need to choose how users register, log in, and get tokens.

Options:

- Clerk (hosted service)
- Keycloak (self-hosted)
- Own implementation

## Decision

|  | Clerk | Keycloak | Own implementation |
|---|---|---|---|
| Who maintains it? | Clerk | We do | We do |
| Effort | Small | Medium (hosting, configuration, upgrades) | Large |
| Cost | Free tier is enough for this project; grows with user count | Server cost | Server cost |
| What I learn | A little | More, full control | A lot, everything built by hand |

We use Clerk.

The main reason is learning. I have already used Keycloak in another project and want to try Clerk.

An own implementation would also be interesting, but this project already has a lot of new things to learn. It is better to leave that for another project.

## Consequences

### Benefits

- No time spent building login UI and authentication.
- Clerk stores user data and handles password security. We never store passwords (NFR-10).
- Our services only validate standard JWTs, so switching providers later mostly affects the frontend.

### Costs

- If Clerk is down, nobody can log in. Search still works because it does not require login.
- All user data lives in Clerk. Services store only the Clerk user ID.
- Booking needs the customer's email from the JWT. It may need to be added to the token as a custom claim.
- Cost may grow with the number of users compared to a self-hosted option.
