# Where to See Withdrawals (Disbursements)

Driver withdrawals are payouts to bank accounts and e-wallets, not Invoices, so they never
appear under Transactions/Invoices in either provider's dashboard.

Which provider handles a given withdrawal depends on `Payments:ActiveGateway` at the time
it was created; existing records always route by the `Provider` stored on the row. Most of
this page is written for Xendit — the PayMongo equivalents are called out inline, and
`docs/PAYMENT_GATEWAYS.md` is the cross-provider reference.

## In the Xendit Dashboard

| What you see in the app | Xendit product | Where in dashboard |
|-------------------------|----------------|---------------------|
| **Invoices** (top-ups, customer payments) | Invoices | **Transactions** / **Invoices** — incoming money |
| **Withdrawals** (driver payouts to bank) | Disbursements | **Disbursements** or **Batch Payouts** — outgoing money |

- **Transactions / Invoices**: Payments *into* your Xendit balance (e.g. driver top-up, booking payment).
- **Disbursements / Payouts**: Money sent *out* to bank accounts (driver withdrawals).

So withdrawal transactions **do not appear under Transactions or Invoices**. They appear under:

- **Disbursements** (or **Batch Payouts** / **Payouts**), e.g.  
  `https://dashboard.xendit.co/` → look for **Disbursements** or **Batch Payouts** in the sidebar or Payments menu.

## Webhook logs

- **Invoice webhooks**: Only for invoice events (e.g. invoice.paid). Used for top-ups and booking payments.
- **Disbursement webhooks**: The backend accepts Xendit payout webhooks (e.g. `payout.succeeded`, `payout.failed`) on the same `/api/webhooks/xendit` endpoint. When configured in Xendit, these update withdrawal status and wallet `PendingPayout` automatically.

Our backend creates disbursements via the API and stores `XenditDisbursementId` on each `WithdrawalRequest`. Status updates are applied via disbursement webhooks when Xendit sends them.

### Webhook security (production)

- **Production**: You must set `Xendit:StrictWebhookValidation` to `true` and configure at least one of: `Xendit:WebhookToken`, `Xendit:PublicKey` (RSA for signature verification), or `Xendit:AllowedSourceIps`. Otherwise webhook requests are rejected with 401.
- **Non-production**: In Development, if no valid auth is provided, the webhook is still accepted and a warning is logged. This is for **testing only**. Do not rely on this in production; always configure token, signature, or IP allowlist before going live.

---

## No transaction history / no webhook logs

If you see **no disbursement in the provider dashboard** and **no new webhook logs**, the
disbursement API call is failing on our backend before it ever reaches the provider.

The withdrawal is **not** marked successful when that happens: `RequestWithdrawalCommandHandler`
calls the gateway *first* and only debits the wallet and writes the `WithdrawalRequest` row
once the provider accepts. A failure therefore leaves **no database row at all** — the log
line is the only record, which is why the log tags below matter.

### 1. Check backend logs

Search your API/backend logs for:

- **`[WITHDRAWAL] [REQUEST] [PAYOUT] Creating`**  
  → The handler reached the gateway. The line names the gateway, amount and bank code.

- **`[WITHDRAWAL] [REQUEST] [PAYOUT] Accepted`**  
  → The provider took the transfer. You should see it in the dashboard under
  Disbursements/Payouts (Xendit) or the Wallet transaction history (PayMongo). If you
  still don't, check you are looking at the same environment as the key (sandbox vs live).

- **`[WITHDRAWAL] [REQUEST] [PAYOUT] Failed`**  
  → The call failed; the logged exception says why. Paired with the gateway's own line:
  **`[PAYMONGO] Transfer failed`** (status, error code, sub-code and PayMongo's `detail`)
  or **`[XENDIT] Payout failed`**.

- **`[WITHDRAWAL] [REQUEST] Unsupported Bank`**  
  → The bank code did not resolve to an institution in `PhBankCatalog`, so nothing was
  sent. The driver is told to pick from the list rather than to retry.

### 2. Check whether a withdrawal row exists

If there is no `WithdrawalRequest` row for the attempt, the provider call failed and was
caught — see the log tags above. If a row exists, `Provider` and `ProviderDisbursementId`
identify the transfer at the provider.

A row stuck in `Approved` means the provider accepted it but never reported a final
status. The `driver-withdrawal-reconcile` job polls those every 30 minutes once they are
20+ minutes old; look for **`[WITHDRAWAL] [RECONCILE]`**.

### 3. Common causes of “Failed”

- **Permissions**: The API key may have access to Invoices but not to Disbursements/Payouts. In Xendit Dashboard → Settings → API Keys / permissions, ensure disbursements/payouts are enabled for the key you use.
- **Environment**: Same API key and same environment everywhere (all sandbox or all live). If the app uses live keys but you’re looking at the sandbox dashboard (or vice versa), you won’t see the transaction.
- **Product/account**: Some accounts have only the newer **Payouts** product (e.g. `POST /v2/payouts`). Our code uses the legacy **`POST disbursements`** endpoint. If Xendit returns 404 or “endpoint not found”, the account may only support the Payouts API and we may need to switch the integration to `v2/payouts` and the new payload format.
- **Bank code / payload**: Wrong or unsupported `bank_code` (or other required fields) can
  cause 400. Check the exact error in the `[XENDIT] Payout failed` /
  `[PAYMONGO] Transfer failed` log. Since the bank catalog landed, the handler rejects an
  unresolvable bank before calling the provider at all.
- **PayMongo: wrong key type.** `PayMongo:SecretKey` must start with `sk_test_` or
  `sk_live_`. A `whsk_…` value is the webhook signing secret; every API call returns 401
  `{"code":"unauthorized","detail":"failed to get organization"}`. Startup validation now
  rejects the wrong prefix.
- **PayMongo: source account not configured.** Without all three of
  `PayMongo:SourceAccountNumber` / `SourceAccountName` / `SourceAccountBic`, payouts throw
  before any HTTP call. Read them from `GET /v2/wallets`. Startup logs an error.
- **PayMongo: wallet not enabled for external transfers.** Sending to banks and e-wallets
  over InstaPay/PESONet needs an Enabled (not closed-loop) wallet, which requires a
  Registered business type. Unregistered accounts can only send to other PayMongo wallets.

### 4. Webhook logs

Disbursement events do **not** appear in **Invoice** webhook logs. So “no new webhook logs” under the invoice webhook is expected. To see disbursement status in webhooks you must configure **Disbursement/Payout webhooks** separately in the Xendit dashboard.

---

## Withdrawal receipt email

When a driver requests a withdrawal, the backend sends a **withdrawal receipt email** to the driver (if the driver has an email in Identity). The receipt includes amount, date, masked account, bank name, and optional Xendit reference/link.

- **Optional config** (in `appsettings` or env): `Xendit:PayoutReceiptUrlFormat` or `Xendit:DashboardBaseUrl` — if set, the receipt email includes a "View transaction status at Xendit" link (e.g. `https://dashboard.xendit.co/payouts`). The payout ID is appended to this base URL. If not set, the email still includes the Xendit reference ID for support.
