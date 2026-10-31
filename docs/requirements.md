# Requirements

Status: draft (Phase 1)

## MVP scope

A multi-hotel booking platform where customers search for room types, book them, pay upfront with Stripe (test mode), and can cancel with a refund.

### In scope

- Room search without login
- Customer registration and login
- Booking a room type for a date range
- Temporary hold during payment
- Full upfront payment with Stripe
- Booking confirmation notification
- Cancellation with refund

### Out of scope

- Admin / hotel staff UI (hotels and rooms come from seed data)
- Booking a specific room number
- Partial payments, deposits, pay at hotel
- Changing an existing booking (cancel and rebook instead)
- Reviews, loyalty programs, discounts

## Functional requirements

### Search

- **FR-1** A visitor can search available room types by city and date range (check-in, check-out) without logging in.
- **FR-2** Search results show hotel, room type, price for the stay, and whether the room type is available.

### Account

- **FR-3** A visitor can register and log in.
- **FR-4** Only logged-in customers can create bookings.
- **FR-5** A customer can see their own bookings and their statuses.

### Booking and hold

- **FR-6** A customer can book one room of a room type for a date range.
- **FR-7** Creating a booking holds the room for 30 minutes. A held room is not available to other customers.
- **FR-8** If payment is not completed within 30 minutes, the hold expires and the room becomes available again.
- **FR-9** The system must never confirm more bookings for a room type and night than the hotel has rooms of that type.

### Payment

- **FR-10** The customer pays the full price when booking, using Stripe.
- **FR-11** A booking is confirmed only after the payment is confirmed by Stripe (not by the frontend redirect).
- **FR-12** Late payment (payment succeeds after the hold expired):
  - If a room of the same type is still available, the booking is confirmed.
  - If not, the customer is notified and fully refunded.
- **FR-13** The customer is never charged twice for the same booking.

### Cancellation

- **FR-14** A customer can cancel a confirmed booking before check-in.
- **FR-15** Refund amount:
  - Cancelled at least 24 hours before check-in: 100% refund.
  - Cancelled less than 24 hours before check-in: 50% refund.
- **FR-16** A cancelled booking releases the room.

### Notifications

- **FR-17** The customer receives a notification when a booking is confirmed, cancelled, or refunded due to late payment.

## Assumptions

- Each hotel has a check-in time and a time zone (seed data). The 24-hour rule is calculated from the hotel's local check-in time.
- Cancellation is not possible after check-in time.
- One booking = one room. Multiple rooms need multiple bookings.
- Prices are fixed per room type per night (no dynamic pricing).

## Non-functional requirements

### Consistency

- **NFR-1** Double bookings must never happen.
- **NFR-2** A customer must never be charged more than once.
- **NFR-3** A successful payment must never be lost, even if the booking system is down. The booking is completed when the system is back up.

### Availability

- **NFR-4** Search works even when the payment service is down. The customer sees a clear message that payments are disrupted and the booking cannot proceed.

### Performance

- **NFR-5** Search responds in under 500 ms (p95) at 50 requests/second.

### Security

- **NFR-6** Users must never see other users' data.
- **NFR-7** Users cannot send requests in another user's name (CSRF).
- **NFR-8** Users cannot inject scripts that run in other users' browsers (XSS).
- **NFR-9** Users cannot inject SQL into database queries (SQL injection).
- **NFR-10** Passwords are never stored in our system. Authentication is handled by Clerk (ADR-002).
- **NFR-11** Public endpoints are rate limited (e.g. login: max 5 attempts/min per IP). DDoS protection is out of scope (infrastructure concern).
- **NFR-12** Card details are never stored in our system.
- **NFR-13** Stripe webhook signatures are verified. Unverified webhook requests are rejected.

## Edge cases

- **Concurrent booking:** Two customers try to book the last room at the same time. The first one gets the hold, the second one gets a "sold out" message. The decision is made atomically in the database.
- **Stripe is down:** The hold stays for the normal 30 minutes. The customer sees a message about payment problems and can retry. If payment does not go through in time, the hold expires normally.
- **Time boundaries:** Cancellation exactly 24 hours before check-in gets a 100% refund; 23:59 before gets 50%. The same rule applies to check-in time and hold expiry. Time is taken from the server and compared to the hotel's local check-in time.
- **Invalid input:** The backend always validates input. UI validation is only for convenience.
  - Check-out is after check-in.
  - Check-in is not in the past.
  - Maximum stay is 30 nights.
- **Duplicate webhook:** Stripe sends the same webhook more than once. The payment only changes while it is `Pending`, so a duplicate event changes nothing.
- **Double payment attempt:** The customer clicks "pay" twice or opens two tabs. One booking has only one payment session; a repeated request returns the existing session.
- **Refund fails:** The customer cancels but the Stripe refund fails. The cancellation succeeds immediately and the room is released. The system retries the refund automatically in the background. If it still fails after several attempts, it is flagged for manual handling.
