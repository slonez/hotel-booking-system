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

Start (each in its own terminal):

```bash
docker compose up -d                        # PostgreSQL on port 5433
dotnet run --project src/BookingService     # gRPC on http://localhost:5232
dotnet run --project src/Gateway            # REST on http://localhost:5172
cd frontend && npm run dev                  # http://localhost:5173
```

## Tests

```bash
dotnet test    # requires Docker (Testcontainers)
```
