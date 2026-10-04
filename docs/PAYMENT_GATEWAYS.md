# Payment Gateways (PayMongo + Xendit)

The backend supports two payment gateways behind a provider-agnostic abstraction
(strategy + adapter). **PayMongo is the active gateway**; Xendit remains fully
implemented and switchable.

## Architecture

- `IPaymentGateway` (Payment module, `Application\Gateways\`) — capability-split
  interfaces (`ICollectionGateway`, `IRefundGateway`, `IDisbursementGateway`,
  `IVaultGateway`) composed into one umbrella. Provider-neutral DTOs + status
  enums; provider-native strings never leave the adapters.
- Adapters (`Infrastructure\Services\`): `XenditGateway` (invoices, v2/payouts,
  refunds, customers/payment_methods) and `PayMongoGateway` (checkout sessions,
  v2/batch_transfers over InstaPay/PesoNet, refunds, customers/payment_methods).
  **PayMongo amounts are integer centavos; the adapter converts both directions.**
- `IPaymentGatewayFactory` — `GetActive()` for NEW records (from
  `Payments:ActiveGateway`), `Get(record.Provider)` for EXISTING records.
- Only providers with credentials configured are registered; asking for an
  unconfigured one throws a clear error.

## Switch semantics (important)

`Payments:ActiveGateway` routes **new** payments, driver top-ups, withdrawals,
and newly saved payment methods only. Refunds, webhooks, and reconciliation
always route by the `Provider` column stored on each record, and **both webhook
endpoints stay registered permanently** — flipping the switch never strands
in-flight records from the other provider.

Consequences:
- Keep Xendit credentials configured until no non-terminal Xendit records remain
  (payments Pending/RefundPending, pending top-ups/withdrawals).
- A saved card vaulted with one provider cannot be charged through the other;
  clients read `payment_gateway` from `GET /api/config` to pick the right
  tokenization SDK (PayMongo.js vs Xendit.js).

## Webhooks

| | Xendit | PayMongo |
|---|---|---|
| Route | `POST /api/webhooks/xendit` | `POST /api/webhooks/paymongo` |
| Auth | any-of: callback token, RSA signature, IP allowlist | HMAC-SHA256 `Paymongo-Signature` (`t=,te=,li=`) + timestamp tolerance (default 300s, replay protection) |
| Idempotency | `PaymentWebhookEvent` unique (provider, event key) | same store, event key = `evt_...` id |
| On processing error | 500 → provider redelivers | 500 → provider redelivers |

Both endpoints share `WebhookEventStore` and are covered by the `/api/webhooks`
rate-limit bucket and request-body buffering.

PayMongo event dispatch: `checkout_session.payment.paid` → payment marked paid
(+ capture id `pay_...` persisted for refunds, driver top-up credited,
`PaymentCheckoutWebhookReceived` published for SSE); `*refund*` →
refund finalization; `transfer.*` → withdrawal completion.

Transfer webhooks may not be enabled on every PayMongo account, so the
`driver-withdrawal-reconcile` Hangfire job (`WithdrawalReconciliationService`, every 30
min) polls `GET /v2/transfers/{id}` for withdrawals still `Approved` after 20 minutes and
resolves them through the same handler the webhook uses. That job is the guaranteed path;
the webhook is an accelerator. Set `PayMongo__TransferCallbackUrl` to have PayMongo POST
status changes directly to `/api/webhooks/paymongo`.

## Go-live checklist (PayMongo)

1. Add secrets to Vault (`bee/config` KV document, `Section__Key` naming):
   `PayMongo__SecretKey` (must start `sk_test_`/`sk_live_` — a `whsk_` value is the
   webhook secret and 401s every call), `PayMongo__WebhookSecret`,
   `PayMongo__CheckoutSuccessUrl`, `PayMongo__CheckoutCancelUrl`,
   `PayMongo__SourceAccountNumber`, `PayMongo__SourceAccountName`,
   `PayMongo__SourceAccountBic`, `Payments__ActiveGateway=paymongo`.

   Read the three source-account values with `./scripts/paymongo-source-account.sh`,
   which prints them as pasteable `.env` lines, or by hand:
   `curl -sL https://api.paymongo.com/v2/wallets/ -u "$PAYMONGO_SECRET_KEY:"` — the
   trailing slash and `-L` matter (the API 301s to it), as does the colon after the key
   (empty password; without it curl prompts). Without all
   three, `DisbursementsConfigured` is false and every withdrawal fails before reaching
   the wire; startup logs an error when the active gateway is PayMongo.

   Outbound transfers to external banks also require an **Enabled** (not closed-loop)
   PayMongo wallet, which needs a Registered business type — an Individual/unregistered
   account can only send to other PayMongo wallets.
2. Register the webhook: `POST https://api.paymongo.com/v1/webhooks` with
   `url=https://<host>/api/webhooks/paymongo` and events
   `checkout_session.payment.paid`, `payment.refund.updated`, `refund.updated`
   (+ `transfer.*` if available). Store the returned `whsk_...` secret as
   `PayMongo__WebhookSecret`.
3. Keep the Xendit webhook registration live until Xendit is decommissioned.
4. Production fail-fast (Program.cs) refuses to start when the active gateway's
   credentials are missing, when a configured gateway has no webhook secret, or
   when `Payments:ActiveGateway` is not `xendit`/`paymongo`.

## Data model

`Payments`, `SavedPaymentMethods`, `DriverTopUps`, `WithdrawalRequests` carry
`Provider` + generic id columns (`ProviderPaymentId`, `ProviderCheckoutUrl`,
`ProviderCaptureId`, `ProviderRefundId`, `ProviderCustomerId`,
`ProviderPaymentMethodId`, `ProviderDisbursementId`). The legacy `Xendit*`
columns are frozen (backfilled, no writers) and are dropped in a follow-up
migration ~2 release cycles after go-live once no non-terminal Xendit records
remain — search for `TODO(provider-cleanup)`.

Saved-payment-method tokens stay encrypted at rest; the encryption purpose value
is the historical literal `"XenditToken"` (see
`DataProtectionPurposes.PaymentProviderToken`) — **never change it**, or every
stored token becomes undecryptable.

## Bank codes

Withdrawal records store friendly bank codes (`BPI`, `BDO`, `GCASH`, ...).
Each adapter normalizes at call time: `XenditBankCodeMap` → `PH_*` channel
codes, `PayMongoBankCodeMap` → BIC identifiers. Parity is enforced by
`BankCodeMapParityTests` so saved withdrawal methods survive a gateway switch.
Unknown codes throw on the PayMongo path (a wrong BIC would misroute money).
