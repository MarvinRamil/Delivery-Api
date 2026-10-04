# Booking Lifecycle Saga — Design

**Status: proposed (not yet implemented).** This document describes how to replace the
polling-based booking dispatch coordination (`BookingBroadcastQueueService`'s three
Hangfire jobs) with a MassTransit saga state machine. It was deliberately split out of
the 2026-06-11 hardening change set (H3 matching, payment/webhook hardening, global
message retry, cache fixes) because it rewires core dispatch behavior and deserves its
own focused change with tests.

## Why

Today the booking lifecycle is coordinated by:

1. `BookingBroadcastConsumer` — reacts to the initial broadcast event.
2. `PulseBroadcastingBookingsAsync` (Hangfire, every 2 min) — re-broadcasts waiting
   bookings and *recovers bookings whose initial event was never consumed* (the code
   documents its own delivery gap).
3. `ProcessExpiredOffersAsync` (Hangfire, every 10 s) — expires offers by polling.
4. `ProcessNextDriverInQueueAsync` (Hangfire, every 10 s) — advances the offer queue,
   with a 30-second "freshly created" grace hack to avoid racing job #3.

State transitions live in four places, timing is polling-based (up to 10 s latency on
every transition), and the jobs race each other. A state machine makes the lifecycle a
single artifact with event-driven timeouts.

## State machine

States:

```
PendingAssignment → Broadcasting → Offered → Accepted → InTransit → Completed
                         │            │
                         │            └─ (offer expired/rejected) → Broadcasting (next driver)
                         └─ (broadcast timeout, no acceptance) → RejectedByAllDrivers
Cancellation (from any pre-Completed state) → Cancelled [→ RefundRequested → Refunded/RefundFailed]
```

Saga state entity (`BookingSagaState : SagaStateMachineInstance`), persisted via
MassTransit EF Core saga repository in `BookingsDbContext` (the outbox is already
configured there, so saga transitions and outgoing messages commit atomically):

- `CorrelationId` = BookingId
- `CurrentState`
- `BroadcastDeadline`, `CurrentOfferId`, `CurrentOfferDriverId`, `OfferSequence`
- `RefundRequested`, timestamps

## Events (existing contracts reused where possible)

| Trigger | Replaces |
|---|---|
| `BookingCreatedEvent` (exists, starts saga) | initial broadcast + Pulse recovery |
| `DriverOfferAcceptedEvent` | accept flow status updates |
| `DriverOfferRejectedEvent` | reject flow + queue advance |
| `OfferExpiredTimeout` (saga-scheduled message) | `ProcessExpiredOffersAsync` polling |
| `BroadcastTimeout` (saga-scheduled message) | "rejected by all drivers" detection |
| `BookingCancelledEvent` | cancellation handling, triggers `RefundPaymentCommand` |
| `PaymentRefundedEvent` (exists) | refund confirmation |

Timeouts use MassTransit message scheduling (`Schedule<OfferExpiredTimeout>` with the
RabbitMQ delayed-exchange plugin or Hangfire scheduler integration — the latter is
already in the stack: `MassTransit.Hangfire`). Each offer schedules its own expiry;
no polling.

## Compensation

- `Cancelled` after payment: saga sends `RefundPaymentCommand` (already idempotent,
  RefundPending-aware after the 2026-06-11 hardening) and waits for
  `PaymentRefundedEvent` / refund failure before finalizing.
- Refund failed: saga parks in `RefundFailed` for back-office attention instead of
  silently completing.

## Offer fan-out decision

Keep `DriverAvailabilityService` (now H3-backed) as a saga activity: on entering
`Broadcasting`, the saga requests candidates and creates offers (sequential or batch
per `BookingSettings:MaxOffersPerBroadcast`), then schedules `OfferExpiredTimeout`.

## Rollout plan

1. Add saga + state table migration, registered behind `BookingSettings:UseSagaOrchestration`
   (default false). Hangfire jobs keep running.
2. Shadow mode: saga consumes events and logs intended transitions without writing
   booking state; compare against Hangfire behavior in staging.
3. Flip flag: saga owns transitions; Hangfire jobs reduced to a reconciliation sweep
   (hourly, alert-only).
4. Delete the three polling jobs once reconciliation stays quiet for a release cycle.

## Test prerequisites (blocking)

There is currently no test project in the solution. Before the saga lands, add one with
MassTransit's `ITestHarness` saga tests covering: happy path, offer-expiry cascade,
all-rejected, cancel-with-refund, refund-failure, and duplicate-event idempotency.
