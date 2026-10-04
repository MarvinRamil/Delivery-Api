# PayMongo Setup Guide

End-to-end setup for PayMongo payments in the Bee backend + driver app: what to get
from the PayMongo dashboard, how to configure the backend, how to wire the webhook, and
how the real-time (SignalR) confirmation flow works.

---

## 1. What you need from the PayMongo Dashboard

PayMongo uses **API keys** — there is **no OAuth `client_id`/`client_secret`** for
standard payments. You need exactly three values:

| Credential | Format | Where in the dashboard | Used by |
| ---------- | ------ | ---------------------- | ------- |
| **Secret key** | `sk_test_…` / `sk_live_…` | Developers → **API Keys** → *Secret key* | Backend — auth for every PayMongo API call |
| **Webhook signing secret** | `whsk_…` | Returned when you **create a webhook** (§4) | Backend — verifies incoming webhook signatures |
| **Public key** *(not needed)* | `pk_test_…` / `pk_live_…` | Developers → API Keys → *Public key* | Client-side tokenization only — **not used**, we use hosted checkout |

> **Why no public key / client id?** The driver app uses PayMongo **hosted checkout**:
> the app just opens the checkout URL and the payer enters card details on PayMongo's
> page. There is no in-app card tokenization, so no public key is required.

Use **test mode** keys (`sk_test_…`) for development; switch to live keys (`sk_live_…`)
only in production.

---

## 2. Backend configuration

PayMongo config lives in the `PayMongo` section, and the active gateway is selected by
`Payment:ActiveGateway`.

### Where config comes from per environment

- **Production / Staging:** all `PayMongo:*` values come from **HashiCorp Vault**
  (the `config` secret, keys prefixed `PayMongo__…`). This is the single source of truth.
- **Development (`dotnet run`):** the app reads `PayMongo` and `Payment` from
  `appsettings.Development.json` (or env vars), **overriding Vault** — so you can use
  sandbox keys locally without touching Vault. This override only applies in Development
  and only to these two sections; DB, RabbitMQ, and encryption keys still come from Vault.

### `appsettings.Development.json`

```jsonc
"Payment": {
  "ActiveGateway": "paymongo"          // makes PayMongo the gateway new payments use
},
"PayMongo": {
  "SecretKey": "sk_test_…",            // REQUIRED — API secret key
  "BaseUrl": "https://api.paymongo.com/",
  "WebhookSecret": "whsk_…",           // REQUIRED for webhooks — signing secret from §4
  "StrictWebhookValidation": false,    // false eases local testing; true in prod
  "WebhookToleranceSeconds": 300,      // reject webhook signatures older than 5 min (replay guard)
  "CheckoutSuccessUrl": "https://…/success",  // REQUIRED by checkout sessions
  "CheckoutCancelUrl": "https://…/cancel",    // REQUIRED by checkout sessions
  "DefaultTransferProvider": "instapay",       // "instapay" (≤ ₱50k) or "pesonet" — payouts only
  "SourceAccountNumber": "",            // payouts/withdrawals only — leave empty to disable
  "SourceAccountName": "",
  "SourceAccountBic": ""
}
```

**Minimum to run:** `Payment:ActiveGateway=paymongo`, `PayMongo:SecretKey`, and the two
checkout URLs. `WebhookSecret` is required for payments to actually confirm (see §5).
The source-account fields are only needed for **driver withdrawals** (disbursements stay
disabled while empty).

Env-var form (highest precedence, also works in dev): `PayMongo__SecretKey=sk_test_…`,
`Payment__ActiveGateway=paymongo`.

---

## 3. The webhook endpoint

The backend exposes a single, unauthenticated endpoint:

```
POST /api/webhooks/paymongo
```

- Reads the `Paymongo-Signature` header and verifies HMAC-SHA256 over `"{t}.{rawBody}"`
  using `PayMongo:WebhookSecret` (constant-time compare, replay-window checked against
  `WebhookToleranceSeconds`).
- Dedupes events via a `WebhookEventStore` (retried deliveries apply once).
- Routes by event-type prefix:
  - `checkout_session.*` → driver top-up crediting
  - `*refund*` → refunds
  - `transfer.*` → disbursement / withdrawal settlement

> In **non-production**, a failed signature logs a warning and proceeds anyway (testing
> convenience). In production, invalid signatures are rejected.

---

## 4. Register the webhook in PayMongo

### 4a. Expose your backend over HTTPS

PayMongo must reach your server publicly.

- **Local dev:** tunnel your API port (e.g. `5001`):
  ```bash
  ngrok http https://localhost:5001
  # or: cloudflared tunnel --url https://localhost:5001
  ```
  Copy the public `https://…` URL. Your webhook URL is that host + `/api/webhooks/paymongo`.
- **Staging/Prod:** use your real domain.

### 4b. Create the webhook

```bash
curl https://api.paymongo.com/v1/webhooks \
  -u sk_test_YOUR_SECRET_KEY: \
  -H "Content-Type: application/json" \
  -d '{
    "data": {
      "attributes": {
        "url": "https://<your-public-host>/api/webhooks/paymongo",
        "events": [
          "checkout_session.payment.paid",
          "payment.paid",
          "payment.failed"
        ]
      }
    }
  }'
```

Add `"refund.updated"` if testing refunds, and the `transfer.*` events if testing driver
withdrawals/payouts. For top-ups, **`checkout_session.payment.paid` is the essential one.**

(The dashboard also has **Developers → Webhooks → Add endpoint** if you prefer clicking.)

### 4c. Save the signing secret

The create response contains the signing secret — **shown once**:

```json
"data": { "attributes": { "secret_key": "whsk_xxxxxxxx", ... } }
```

> ⚠️ In the response this field is named `secret_key`, but it is the **webhook signing
> secret** (`whsk_…`), NOT your API secret key. Put it in `PayMongo:WebhookSecret`.

**Manage webhooks:** `curl https://api.paymongo.com/v1/webhooks -u sk_test_YOUR_SECRET_KEY:`
lists them. You cannot re-read a secret after creation — only disable / recreate.

---

## 5. How confirmation reaches the app (the "websocket" / real-time flow)

Payment confirmation is **push-based**, not polled. The client is notified over
**SignalR** (WebSockets) the moment the webhook is processed:

```
Payer pays on PayMongo hosted checkout
        │
        ▼
PayMongo ──POST──▶ /api/webhooks/paymongo   (signature verified, deduped)
        │
        ▼
checkout_session.payment.paid ──▶ PaymentCheckoutWebhookReceived (MassTransit)
        │
        ▼
DriverTopUpWebhookConsumer ──▶ ProcessDriverTopUpWebhookCommand
        │  (credits wallet, marks top-up Paid)
        ▼
DriverTopUpEventBroadcaster ──▶ SignalR "TopUpPaid" to group "driver-{driverId}"
        │
        ▼
Driver app (walletTopUpEventsService) ──▶ balance refreshes live, no reopen
```

### SignalR details

- **Hub URL:** `/hubs/notifications`
- **Group:** the app calls `JoinGroup("driver-{driverId}")` on connect.
- **Event name:** `TopUpPaid`
- **Payload:**
  ```json
  {
    "topUpId": "…",
    "driverId": "…",
    "amount": 500.0,
    "status": "Paid",
    "personalBalance": 0.0,
    "topUpBalance": 500.0,
    "paidAtUtc": "2026-07-03T…Z"
  }
  ```
- **Client:** `bee-driver/features/wallet/services/walletTopUpEventsService.ts` connects with
  the auth token, joins the driver group, and calls `onTopUpPaid` to refresh the wallet.
- **Server:** `bee-backend/src/BeeLogistics.Api/Services/DriverTopUpEventBroadcaster.cs`
  emits the event (it also supports an SSE fallback).

> This is separate from the payment itself — the webhook does the crediting; SignalR just
> notifies the UI. If SignalR is down, the balance is still correct on the next wallet fetch.

---

## 6. End-to-end test

1. Fill `PayMongo:SecretKey` + `PayMongo:WebhookSecret` (+ checkout URLs) in
   `appsettings.Development.json`, and ensure `Payment:ActiveGateway=paymongo`.
2. `dotnet run` — look for the log line:
   `Development: N payment config keys sourced from appsettings (overriding Vault).`
3. Start a tunnel (§4a) and register the webhook (§4b) pointing at it.
4. In the driver app: **Wallet → Top up** → enter amount → open the checkout URL →
   pay with a PayMongo **test card**.
5. Confirm:
   - API logs show `[PAYMONGO] [WEBHOOK]` receipt + signature verification.
   - The top-up flips to **Paid** and the wallet's top-up balance increases.
   - The app updates **live** (SignalR `TopUpPaid`) without manual refresh.

---

## 7. Production checklist

- [ ] `PayMongo:*` values stored in **Vault** (`config` secret), not appsettings.
- [ ] Live keys (`sk_live_…`) and a **live-mode** webhook registered on the prod URL.
- [ ] `PayMongo:StrictWebhookValidation = true`.
- [ ] `Payment:ActiveGateway = paymongo` in the Vault config.
- [ ] Source-account fields set **only if** driver withdrawals are enabled.
- [ ] Webhook URL uses your real HTTPS domain (no tunnel).

---

## Quick reference

| Item | Value |
| ---- | ----- |
| Webhook endpoint | `POST /api/webhooks/paymongo` |
| Key config section | `PayMongo` (+ `Payment:ActiveGateway`) |
| API secret key | `PayMongo:SecretKey` (`sk_…`) |
| Webhook signing secret | `PayMongo:WebhookSecret` (`whsk_…`) |
| Essential webhook event | `checkout_session.payment.paid` |
| SignalR hub / event | `/hubs/notifications` / `TopUpPaid` |
| Dev config source | `appsettings.Development.json` (overrides Vault, Development only) |
| Prod config source | HashiCorp Vault (`config` secret) |
