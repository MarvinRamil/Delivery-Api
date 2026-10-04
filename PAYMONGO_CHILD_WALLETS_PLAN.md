# Driver wallets on PayMongo child accounts

**Status:** proposal, verified against live PayMongo on 2026-08-30
**Author:** spike + plan from the withdrawal work following issues #89 / #90

---

## 1. Why

We hold driver money in our own database and push it out over InstaPay. Issue #90 exists because of
that: reserve-before-send ordering, an `xmin` optimistic-concurrency guard, a reconciliation poller,
double-spend detection. All of it is custody machinery — code whose only job is to stop us paying out
money a driver does not have.

PayMongo can hold that balance instead, one wallet per driver, and enforce the overdraw rule itself.

**Everything in section 2 was exercised on live**, not read from docs. A real consumer child account
was created, KYC'd through PayMongo's hosted flow, activated, funded, and paid out from — both from
PayMongo's dashboard and from our own backend over the API.

Parent account: `org_bUhq37yJjSDqQTMydiAxqXVJ`
Probe child: `org_5bff545e74b5f872b1dc6818` / `wallet_3e901a16095487630f6ed17a`

---

## 2. What is proven

| Capability | Result |
| --- | --- |
| `POST /v2/accounts` (consumer child) | ✅ `201` |
| Parent↔child relationship auto-forms | ✅ `enabled: true`, `linking_request_id: null` |
| Hosted KYC (PowerCred), real ID + selfie | ✅ `identity_verification_status: passed` |
| `POST /v2/accounts/{c}/activate` | ✅ `activation_status: activated` |
| Child receives its own wallet | ✅ **`type: "default"`** — not closed-loop |
| Parent → child transfer | ✅ ₱40, **fee 0**, `provider: "paymongo"` |
| Child → external e-wallet (dashboard) | ✅ ₱20 succeeded |
| **Backend-initiated payout from child wallet** | ✅ `POST /v2/batch_transfers` + `Account-Id` → `201` |
| Read child balance | ✅ `GET /v2/wallets/?fields=balance` + `Account-Id` |
| Read child account number | ✅ `?fields=account` → `285168654745`, `ledger_account_id` |
| Read child limits | ✅ ₱500,000 cap, ₱500,000/day outward |
| Scoped transfer history | ✅ `GET /v2/transfers/` + `Account-Id` |
| Failure reason | ✅ `provider_error_message` |

### 2.1 API quirks — write these down, they cost hours

| Quirk | Consequence |
| --- | --- |
| `fields` takes **one value per call**. `?fields=balance,account` returns `null` for both. | Two calls to get balance + account number. |
| `Account-Id` works on **`/v2` only**. On `/v1/wallets/` it is **silently ignored** — a bogus org id returns the **parent's** wallet with `200`. | Use v2 everywhere. If a v1 call is unavoidable, assert the returned wallet id. |
| Identity verification returns `url`, **not** `hosted_url` as documented. | Code written from the docs hands drivers a `null` link. |
| Child is **read-only after activation** — `PATCH` → `400 "Account is not in pending activation status."` | Whatever PowerCred's OCR extracted is frozen. Our probe is permanently `first_name: 'GREGORIO "GAYLE" III DADULLA'`. **Review the parsed name before activating.** |
| **`POST /v2/accounts` silently discards the `person` object.** Email and mobile passed at creation come back `null`. PayMongo's quick start shows them in that body anyway. | Set them on the `PATCH`, or activation fails demanding fields you believe you already sent. |
| **`tin` is NOT required for consumer activation**, despite the activation guide listing it as a prerequisite. | Verified by activating an empty child and reading the enumerated required set. See below. |
| Email must be **globally unique across PayMongo** (`409` otherwise), and is frozen at activation. | Three-rung ladder, retried on the **same** child account so a collision costs nothing: (1) the driver's own address; (2) `{local}+bee{token}@{their domain}` — distinct to PayMongo but delivered to the same inbox by Gmail and most providers, so notices and password resets still reach them; (3) `driver+{token}@ourdomain`, only when they have no email on file. The token is 6 base36 characters derived deterministically from the driver id, so a retry yields the same address; a token collision is harmless because it just falls to the next rung. The accepted address is stored on `DriverWallet.PayMongoAccountEmail`, since support cannot infer which rung won. |
| Failure reason is `provider_error_message`; there is no `failure_code`/`failure_message`. | e.g. `"validation failed for transfer_validator.source_account_balance: insufficient"` |
| A **failed transfer costs nothing** — the record showed `fee: 1000` but neither balance moved. | Do not treat a non-zero `fee` on a failed transfer as spend. |

### 2.1a What activation actually requires

Measured by creating a child and calling `/activate` with nothing filled in. PayMongo enumerates the
full required set in the rejection, one `parameter_required` per field:

```
first_name              last_name
mobile_number           email_address
date_of_birth.day       date_of_birth.month      date_of_birth.year
nationality             nature_of_work
place_of_birth.city     place_of_birth.country
source_of_funds
address.line1  address.city  address.state  address.postal_code  address.country
```

Seventeen fields, and **`tin` is not among them.**

`first_name`, `last_name` and `date_of_birth` are filled by identity verification from the ID, so
the app only has to collect the rest.

> **This retires the biggest risk in the plan.** §7.1 previously called the mandatory TIN the thing
> most likely to kill this architecture, because many riders do not have one. It is optional, so it
> gates nobody. The field stays in the UI marked optional.

### 2.2 The transfer fee

**The ₱10 is debited from the source wallet — the driver's.** Proven by a payout failing for
insufficient funds: the child held ₱20 and a ₱20 transfer needed ₱20 + ₱10.

Observed fees: dashboard-initiated transfers `fee: 0`; both API-initiated ones `fee: 1000`;
in-network parent→child `fee: 0`. The rule is **unconfirmed** — it may be channel-based, or
one-free-per-week-per-account. **Read the `fee` field, never hardcode ₱10.**

> **Design consequence.** A driver can never withdraw their entire balance. The withdraw screen must
> cap at `balance − fee` and the backend must reject anything above it, or every "withdraw all"
> fails. Our current UI has no concept of this.

---

## 3. Target architecture

**PayMongo holds the withdrawable balance. We keep everything that is a receivable.**

| Concern | Owner | Why |
| --- | --- | --- |
| Driver withdrawable balance | **PayMongo child wallet** | They enforce the overdraw rule |
| Driver cash-out | **PayMongo**, initiated by our backend via `Account-Id` | Stays in our app, white-label |
| `TopUpBalance` COD credit line | **Ours** | A wallet cannot go negative; ours goes to −₱500 |
| Refund clawback after cash-out | **Ours** | `SubtractForRefund` deliberately allows negative |
| `EarningsSplit` / `PlatformCommissions` | **Ours** | Issue #28's single definition |
| `accounting.LedgerEntries` | **Ours** | Ledgers-as-a-Service is ₱20,000 — not worth it |

### 3.1 Coexistence, not deletion

**Nothing is deleted.** This is a money system and the existing path is hardened, tested and
working. The new path runs alongside it behind flags; the old one stays as the fallback until the
new one has proven itself on real drivers over real time.

An earlier draft of this claimed ~3,000 lines deleted. That was wrong twice over — PESONet
reintroduces in-flight state that needs tracking, and a big-bang cutover on custody code is not a
risk worth taking for line count.

#### The natural per-driver flag

`DriverWallet.PayMongoAccountId != null` **is** the flag. No separate toggle needed:

- driver has a linked, activated child account → new path
- null → existing path, byte-for-byte unchanged

That gives per-driver rollout for free: onboard one driver, then ten, then the rest. A driver who
has not been migrated is running exactly the code that runs today.

> **The flags are not independent.** `EarningsPushEnabled` on with `WithdrawalsEnabled` off would
> otherwise pay a migrated driver twice — their earnings sit in their child wallet while the
> platform path reserves against the local mirror and disburses from the **platform** wallet.
> `RequestWithdrawalCommandHandler` therefore refuses outright for any driver whose balance is at
> PayMongo when the child path is disabled. Turning withdrawals off **pauses** them; it never
> reroutes them. Enable earnings push only together with, or before, withdrawals.

#### Three config kill switches

In `DriverWalletOptions`, all defaulting to `false`, one per phase so each can be enabled and
reverted independently:

| Flag | Gates |
| --- | --- |
| `PayMongoChildWallets:OnboardingEnabled` | creating/activating child accounts |
| `PayMongoChildWallets:EarningsPushEnabled` | routing the driver's share to their child wallet |
| `PayMongoChildWallets:WithdrawalsEnabled` | the new withdrawal proxy |

With all three off, the new code is unreachable and behaviour is identical to today.

#### What eventually goes — marked, not removed

Once phase 3 has been stable for a meaningful period, mark these `[Obsolete]` so the compiler
surfaces remaining callers, and only delete in a separate, later MR:

- `DriverWallet.PendingPayout` + `RequestWithdrawal` / `CompleteWithdrawal` / `RejectWithdrawal`
  (`DriverWallet.cs:134-173`)
- reserve-first ordering and `xmin` race handling (`DriverHandlers.cs:701-750`)
- `definitelyNotSent` triage (`:791-798`) and `RefundReservationAsync` (`:915`)
- `ReleaseWithdrawalReservationCommand(Handler)` (`:1714`),
  `AdoptWithdrawalDisbursementCommand(Handler)` (`:1768`)
- balance integrity check (`:525-548`), double-spend detection (`:1861-1868`)

**Do not obsolete these during phases 1–2** — they are the live path for every unmigrated driver.

#### What is shared by both paths and never goes

- **`WithdrawalRequest`** — becomes the transfer record for the new path too (driver, amount, rail,
  fee, `ProviderDisbursementId`, status), just without reservation semantics.
- **`PhBankCatalog`** / `PayMongoBankCodeMap` — rail selection and per-rail BIC resolution.
  `BicFor(rail)` already does exactly this.
- **`BankPickerModal.tsx`**, `banks.ts`, `useBanks.ts`, `SavedWithdrawalMethod`, `QrScannerModal.tsx`
- **A reconciliation job** — PESONet needs it (§4.2).

The prize is not line count. It is that **overdraw becomes structurally impossible** for migrated
drivers rather than defended by our own concurrency code, and that we stop holding their funds.

### 3.2 Rollback

Phase 1 is free to reverse — a child account with no money in it can simply be ignored, and the
driver falls back to the old path the moment `PayMongoAccountId` is cleared.

**Phase 2 is the point where reversal costs something**, because the driver's balance now physically
sits at PayMongo. It is still recoverable: sweep child → parent with an in-network transfer (free,
`provider: "paymongo"`), credit the local mirror, clear `PayMongoAccountId`. Write and test that
sweep **before** enabling `EarningsPushEnabled`, not after — an untested rollback is not a rollback.

---

## 4. Rails: InstaPay *and* PESONet

| | InstaPay | PESONet |
| --- | --- | --- |
| Speed | Real-time | Same or next **banking day** |
| Hours | 24/7 incl. weekends/holidays | Banking days only |
| Per-transaction cap | ₱50,000 | ₱10,000,000 |
| Fee | ₱10 (see §2.2) | ₱10 |

For drivers, PESONet matters **less for the amount cap than for institution coverage** — some
institutions in `PhBanks.json` support only one rail. Offering both widens where a driver can cash
out.

### 4.1 Rail selection

In the withdrawal handler, before calling PayMongo:

1. Resolve the destination institution from `PhBankCatalog`.
2. If it supports InstaPay **and** `amount ≤ ₱50,000` → `provider: "instapay"`.
3. Else if it supports PESONet → `provider: "pesonet"`, and tell the driver it lands next banking day.
4. Else reject naming the reason — never silently switch rails.
5. Resolve the BIC with the existing `PhBankCatalog.BicFor(rail)` — **BICs differ per rail**, which
   is why that method exists.

Do not silently fall back from InstaPay to PESONet the way `PayMongoGateway.cs:277-279` used to. A
driver expecting instant money and getting it on Monday is a support ticket.

> **To test in phase 0:** PayMongo's Workflows docs mention `provider: "auto"`, which routes by
> destination and amount. If `batch_transfers` accepts `auto`, steps 1–5 collapse into one field and
> `PhBankCatalog` shrinks to a display-only concern. Untested — one small transfer settles it.

### 4.2 In-flight state

InstaPay is effectively synchronous; PESONet is not. So:

- Persist the `WithdrawalRequest` with the transfer id and `Pending` status.
- Show the driver "arriving next banking day" for PESONet.
- Webhook (`callback_url`) flips it to Completed/Failed.
- Keep a **reduced** reconciliation job for transfers still `Pending` past threshold — 20 minutes for
  InstaPay, one banking day for PESONet — polling `GET /v2/transfers/{id}` with `Account-Id`. This is
  the existing `WithdrawalReconciliationService` with the release/adopt logic removed, since there is
  no reservation to release.

---

## 5. Data model

New columns on `DriverWallet` (one migration in `Modules.Drivers/Infrastructure/Migrations/`):

| Column | Example | Why |
| --- | --- | --- |
| `PayMongoAccountId` | `org_5bff545e…` | The `Account-Id` header value |
| `PayMongoWalletId` | `wallet_3e901a16…` | Assert responses match this |
| `PayMongoAccountNumber` | `285168654745` | `destination_account.number` on every earnings push |
| `PayMongoLedgerAccountId` | `b5089e33-…` | Per-driver reconciliation against PayMongo's ledger |
| `PayMongoActivationStatus` | `pending` / `activated` / `declined` | Onboarding state machine |

**Cache the account number.** A freshly activated child has no transfer history to recover it from,
and `batch_transfers` needs it on every push.

`Balance` stays as a **local mirror**, not a proxy. Reading PayMongo on every screen load is a
network call per request and a hard dependency on their uptime. Update the mirror on transfer and
webhook, reconcile against `?fields=balance` on a schedule, alert on drift — the same shape as the
existing `GetCalculatedPersonalBalanceAsync` check.

---

## 6. Ledger

Simpler, because paying into the driver's own wallet **settles** the liability instead of deferring
it. `PENDING_PAYOUT` has no meaning once nothing is reserved.

```
earning   Dr CUSTOMER_PAYMENT        Cr PLATFORM_REVENUE + DRIVER_PERSONAL_WALLET   (unchanged)
payout    Dr DRIVER_PERSONAL_WALLET  Cr PAYMONGO_OUT
```

This replaces the Requested / Completed / Failed triple with a single entry pair at the moment of
transfer. Add `PAYMONGO_OUT` to `AccountCode`; retire `PENDING_PAYOUT` once no rows reference it.

Every wallet exposes `ledger_account_id`, so per-driver reconciliation against PayMongo's own ledger
is possible **without** buying Ledgers-as-a-Service.

---

## 6a. Passing PayMongo's account costs to the driver

We are billed ₱30 once per wallet and ₱15/wallet/month. The decision is to recover both from the
driver: ₱30 when their wallet is opened, ₱15/month thereafter, with notice.

### Sequencing: never be exposed

Onboarding is ordered so PayMongo never bills us before the driver has paid. The app already
requires a ₱1,000 opening top-up, which gives us the moment to collect:

```
1. register              Didit KYC (ours) — no PayMongo cost
2. top up ₱1,000         existing DriverTopUp checkout → TopUpBalance = ₱1,000
3. debit ₱30             TopUpBalance = ₱970,  Cr PLATFORM_REVENUE
4. create child account   POST /v2/accounts
5. PayMongo KYC           POST /identity_verification   ← the ₱30 we already collected
6. activate               wallet exists; ₱15/month begins
```

A driver who never tops up never gets a PayMongo account, so they cost nothing and there is nothing
to deactivate. This removes most of the inactive-driver exposure without needing the deactivation
route that §10.4 asks about.

**This rests on two assumptions to confirm with PayMongo (§10):** that a `pending` child account
with no KYC run is free, and that the ₱30 is billed at `identity_verification` rather than at
account creation. Both look right — ₱15 is priced as *wallet* maintenance and a pending child has no
wallet (we watched `wallet_3e901a16…` appear only at `activate`), and ₱30 is priced as *onboarding
KYC* — but neither is confirmed, and there is no billing endpoint to check against.

### The monthly fee collects itself

The ₱970 of remaining float sits in `TopUpBalance`, and ₱15/month debits against it for roughly five
years before running dry. It only becomes a receivable if a driver burns their float down through
COD settlements — at which point the existing −₱200 cash-job block already forces a top-up.

We are therefore not extending credit; we are drawing down a float the driver funded.

### The ₱30 split is bookkeeping, not a transfer — keep it in the backend

The driver's ₱1,000 is a *payment* that settles into the parent wallet in full. The ₱30 is the
difference between crediting `TopUpBalance` ₱1,000 and crediting ₱970 — same cash, one wallet, no
counterparty. **A PayMongo Workflow has nothing to move here.** Reasons to keep it in our code:

- No money movement to orchestrate; `send_money` has no source/destination pair.
- The ₱30 needs a `WalletTransaction` and a ledger pair for audit regardless, so a Workflow would
  only duplicate what the backend must already do.
- Issue #28: two implementations of one split drifted by centavos and unbalanced the ledger, which
  is why `EarningsSplit.For` is the single definition. Workflow YAML would create a second one.
- Workflows trigger on `payout.deposited` — settlement, not payment. The driver's float should not
  wait on settlement when the payment webhook already confirms it.

Workflows may earn their place later for the **earnings split** (parent wallet → child wallets),
which is real movement between wallets. Even there, keep the computation in `EarningsSplit` and let
PayMongo do only the moving.

### Where the money physically sits

| Bucket | Cash held by | Record | Can go negative |
| --- | --- | --- | --- |
| `TopUpBalance` (₱970 float, COD settlement) | **our parent wallet** | our DB | yes, to −₱500 |
| Earnings | **driver's child wallet** | PayMongo | no |

The float must stay internal precisely because it goes negative. The child wallet only ever holds
earnings.

> Existing property, unchanged: the ₱970 is credited on the payment webhook, but the cash reaches
> the parent wallet on PayMongo's settlement schedule. We front the float across that gap — true of
> top-ups today, but worth watching once it is ₱1,000 per driver at scale.

### Why not debit the PayMongo wallet directly

At KYC time the driver has no wallet — it is provisioned at activation, *after* verification, and it
is empty. So neither fee can come from the child wallet. Both are settled against `TopUpBalance`,
which the ₱1,000 opening top-up funds (`CreateDriverTopUpCommandHandler`,
`DriverHandlers.cs:1049` — the existing checkout flow, unchanged).

### Charge `TopUpBalance`, not the PayMongo wallet

`TopUpBalance` is already a credit line that goes negative to −₱500
(`DriverWallet.DebitTopUpForCashSettlement`, `DriverWallet.cs:55`). That is exactly the shape of an
account fee: a receivable that always posts and is settled later out of top-ups or earnings.

Sweeping ₱15/month out of each driver's PayMongo wallet instead would be possible — child → parent
in-network is free and proven — but it is worse on three counts: it fails whenever the wallet is
empty, it generates one API call per driver per month (1,000 drivers = 1,000 transfers), and it
needs its own retry and reconciliation. Debiting `TopUpBalance` always succeeds and needs none of
that.

Collection then happens for free through machinery that already exists: the cash-job block at
−₱200 (`IsEligibleForCashJobs`, `:115`) means a driver who lets fees pile up stops getting cash
jobs until they top up.

### Implementation

- `WalletTransactionType.AccountFee = 10` (next free value; the enum currently ends at
  `EarningReversal = 9`).
- `DriverWallet.ChargeAccountFee(decimal amount, decimal allowedNegativeLimit)` — mirrors
  `DebitTopUpForCashSettlement`, debits `TopUpBalance`, enforces the floor.
- **KYC fee** debited from `TopUpBalance` once the opening top-up has cleared, immediately before
  `POST /v2/accounts`. If the balance does not cover it, onboarding does not start.
- **Monthly fee** via a Hangfire job alongside the existing reconciliation jobs in
  `BeeLogistics.Api/Services/`, iterating drivers with an activated child wallet.
- **Idempotency** reuses the existing unique filtered index on
  `(WalletId, Type, ProviderPaymentId)` (`DriversDbContext.cs:142-144`) with
  `ProviderPaymentId = "acctfee-2026-09"`. A re-run of the monthly job cannot double-charge.
- Config in `DriverWalletOptions`: `KycFeeAmount` (₱30), `MonthlyWalletFeeAmount` (₱15),
  `AccountFeesEnabled` (default `false`, fourth flag alongside the three in §3.1).
- **The onboarding gate is the ₱30 fee, not the ₱1,000 float.** The float is a separate business
  rule about accepting jobs and is not enforced here — a driver with ₱30 can open a wallet. Do not
  add a `RequiredOpeningFloat` setting unless something actually enforces it; config that implies a
  gate nobody checks is worse than no config.
- Ledger: `Dr DRIVER_TOPUP_WALLET → Cr PLATFORM_REVENUE`, via a new consumer following the
  `CashSettlementDebitedAccountingConsumer` pattern.
- Notification ahead of each monthly charge, reusing the Notification module's existing email
  consumer pattern.

### Risks specific to charging these on

1. **Disclosure is a requirement, not a nicety.** A recurring charge needs clear consent before the
   wallet is created — fee schedule on the onboarding screen, accepted explicitly, and recorded. This
   should be reviewed against the driver T&Cs before `AccountFeesEnabled` goes on.
2. **Zero margin on the monthly fee.** The ₱30 is now collected up front so it cannot go bad, but
   the ₱15/month still accrues. Charging exactly ₱15 means we break even only on drivers who
   eventually settle; an inactive driver's accrued fees are a straight loss of what PayMongo billed
   us. Worth deciding deliberately whether to recover at cost, mark up, or absorb.
3. **Inactive drivers — mostly solved by sequencing, not by deactivation.**

   Probed on live: wallets have exactly two statuses, `activated` and `deactivated` (the latter
   documented as the *default*), and `GET /v2/wallets/?status=…` filters on it correctly. But **no
   route sets it** — `/v2/accounts/{child}/deactivate` and `/close` both return `404 not_found`, and
   there is no billing or invoice endpoint to inspect either. Deactivation is not available to us
   today (§10.4).

   The sequencing above handles most of this: a driver who never pays the ₱1,000 never gets an
   account, so there is nothing to deactivate. For a driver who funds their float and *then* goes
   quiet, the ₱15/month keeps drawing against their remaining ₱970 — which is the correct outcome,
   not a loss.

   The residual exposure is a driver whose float reaches zero and stays inactive. Sweep their child
   wallet balance back to the parent (free, in-network) and stop accruing on our side; we keep
   paying PayMongo until they answer §10.4.

   > **This is also the strongest argument for keeping Didit.** If PayMongo's KYC replaced Didit at
   > registration, we would pay ₱30 for *every registrant*. Keeping Didit as the registration KYC is
   > what lets us defer PayMongo onboarding to the ₱1,000 top-up. That, more than the duplicate
   > selfie, is why Partner Verification (§10.2) matters.
4. **Fee-only negative balances block cash jobs.** A driver sitting at −₱200 purely from accrued
   fees, having never taken a cash job, would be blocked by `IsEligibleForCashJobs`. Decide whether
   fee debt and cash-settlement debt should share one threshold or be tracked separately.

## 6b. Xendit removal

Xendit is no longer used. There are **654 references across `src/` and `tests/`**, but that number
is misleading — most sit in EF migration files, which must not be touched.

### Delete outright

```
src/Modules/BeeLogistics.Modules.Payment/Infrastructure/Services/XenditGateway.cs
src/Modules/BeeLogistics.Modules.Payment/Infrastructure/Services/XenditBankCodeMap.cs
src/Modules/BeeLogistics.Modules.Payment/Application/Options/XenditOptions.cs
tests/BeeLogistics.Tests/Payments/XenditGatewayTests.cs
docs/XENDIT_DISBURSEMENTS_AND_WITHDRAWALS.md
```

`BankCodeMapParityTests` exists only to assert every `XenditBankCodeMap` key resolves in the
PayMongo map — it goes with them.

### Edit carefully

| File | Refs | What |
| --- | --- | --- |
| `Payment/Presentation/Controllers/WebhooksController.cs` | 41 | Xendit webhook endpoint + signature verification |
| `Payment/Presentation/Controllers/PaymentsController.cs` | 17 | provider branching |
| `BeeLogistics.Api/Program.cs` | 16 | DI registration, startup validation |
| `Payment/DependencyInjection.cs` | 12 | HTTP client + resilience policies |
| `Payment/Infrastructure/PaymentDbContext.cs` | 14 | entity config for the frozen columns |
| `Payment/Application/Handlers/SavedPaymentMethodHandlers.cs` | 14 | vault/tokenisation branching |
| `BeeLogistics.Api/Services/BookingPaymentEventBroadcaster.cs` | 12 | provider naming |
| `tests/.../RefundLifecycleTests.cs` | 25 | Xendit refund cases |
| `tests/.../PaymentGatewayFactoryTests.cs` | 13 | factory now resolves one provider |

### Do not touch

- **Every file under any `Infrastructure/Migrations/` directory**, including `.Designer.cs` and the
  model snapshots. These are the historical migration chain —
  `20260221114944_EncryptXenditTokens`, `20260702061407_AddPaymentProviderColumns`,
  `20260702061441_AddDriverProviderColumns` and the rest. Editing or deleting them breaks EF's
  history and every existing database. Columns are removed by *adding* a migration, never by
  rewriting an old one.
- **`AccountCode.XENDIT_OUT`.** `accounting.LedgerEntries` is append-only and immutable, and
  historical entries reference this code. Removing it orphans them. It stays, alongside the new
  `PAYMONGO_OUT`.

### The frozen columns: keep them

`XenditInvoiceId`, `XenditInvoiceUrl`, `XenditDisbursementId` and the encrypted token columns hold
the audit trail for every payment and payout that actually went through Xendit. **Recommend keeping
them**, marked `[Obsolete]` in the entities so no new code writes to them. Dropping them destroys
historical records for a saving of a few unused columns.

Before deciding either way, run the count:

```sql
SELECT provider, count(*) FROM payment."Payments"       GROUP BY provider;
SELECT "Provider", count(*) FROM drivers."DriverTopUps" GROUP BY "Provider";
SELECT "Provider", count(*) FROM drivers."WithdrawalRequests" GROUP BY "Provider";
```

If rows exist, keep the columns. If genuinely zero everywhere, a drop migration is defensible — but
as its **own MR**, after this one, so a schema deletion is never buried in a feature diff.

### Sequencing

Keep the Xendit removal as **separate commits** from the PayMongo work on the same branch, so the
diff stays reviewable. A 650-reference cleanup mixed into a new-architecture MR is not something
anyone can review honestly. If the branch grows unwieldy, split it into its own MR — the two changes
have no dependency on each other.

## 6c. Driver app: terminology and the activation flow

*(bee-driver work — belongs on the driver issue, listed here so both sides stay in step.)*

### The problem

`app/(tabs)/wallet.tsx` already shows a driver two things called "wallet":

```
:568   "Personal Wallet (Net Earnings)"
:779   "Top-up Wallet"
```

Adding a third — "create your PayMongo wallet" — makes it unnavigable. "Top-up Wallet" is also
named after the *action* rather than the purpose, so a driver tops up their top-up wallet, which is
circular and never says the bucket exists to cover cash collected from customers.

### Never expose wallet creation

The child account is infrastructure. There is no "create wallet" button anywhere. The driver
activates by funding their starting float; account creation, PowerCred KYC and activation all happen
behind that.

```
register + Didit KYC          (existing)
   ↓
"Add your starting float — ₱1,000"
   ₱30    one-time account fee
   ₱970   your cash float
   ₱15    monthly upkeep
   "Your float stays yours — move it to Earnings and withdraw it any time."
   ↓
pay (existing DriverTopUp checkout)
   ↓
"Setting up your account…"     ← child account + KYC + activate
   ↓
Earnings   |   Cash Float
```

That screen is also where the fee disclosure lives, satisfying the consent requirement in §6a risk 1.
The refundability line is accurate: `TransferTopUpToPersonal` (`DriverWallet.cs:101`) already lets a
driver move float back to Earnings and withdraw it, less fees and any COD debt.

### Terminology

| Today | Line | Proposed |
| --- | --- | --- |
| "Personal Wallet (Net Earnings)" | `:568` | **Earnings** — *"Yours to withdraw"* |
| "Top-up Wallet" | `:779` | **Cash Float** — *"Covers cash you collect from customers"* |
| "Transfer Wallet Balance" | `:1395` | **Move money** |
| "From Personal" / "From Top-up" | `:1416` / `:1435` | **From Earnings** / **From Cash Float** |
| "Create Top-up" / "Top Up" | `:1350` / `:839` | **Add funds** |
| "System Commission" | `:679` | **Platform fee** |

### Withdraw screen — three changes forced by what we measured

1. **Show the fee**, read from the API rather than hardcoded (§2.2 — it varied per transfer).
2. **Cap the amount at `balance − fee`.** A driver requesting their full balance currently gets a
   failure; we reproduced exactly this (`source_account_balance: insufficient`).
3. **Rail badge and pending state** — "Instant" for InstaPay, "Arrives next banking day" for
   PESONet, with a pending state for the latter. `BankPickerModal` already renders per-rail data.

### Dependency worth naming

Without Partner Verification, "Setting up your account…" is **not** invisible — the driver does a
second selfie and ID for PowerCred immediately after Didit, and "why am I verifying again?" becomes
a support ticket per driver. With it, the step is genuinely background. This is the third
independent argument for §10.2, alongside the ₱30 and the ability to defer onboarding.

## 7. Risks

1. ~~**TIN is mandatory.**~~ **Resolved 2026-08-30: it is not.** Activating an empty child returns
   the full required set and `tin` is absent from it (§2.1a). This was the risk most likely to kill
   the architecture; it does not exist.
2. **A decline is final** for that `org_`. PayMongo's risk review can reject a driver we would have
   accepted, and there is no appeal path. We would be outsourcing a business decision.
3. **Email aliases.** `driver+{id}@ourdomain` means the driver does not control their own PayMongo
   contact address — no password reset, no PayMongo notifications. Possibly a compliance concern.
4. **Working capital.** Driver earnings must be actually pre-funded at PayMongo rather than being a
   number in a row. Treasury decision, not an engineering one.
5. **Wallet limits.** ₱500,000 balance cap and ₱500,000/day outward per child. Fine for drivers;
   confirm the **parent's** outward limits carry the whole fleet's daily payout volume.
6. **New external dependency on the payout path.** Today a PayMongo outage blocks withdrawals. After
   this, it also blocks earnings landing and balance display.

---

## 8. Phasing

Each phase is one flag. All flags off = today's behaviour exactly.

1. **Onboarding only** — `OnboardingEnabled`. Create + KYC + activate children, store the ids. No
   money moves. Fully reversible. Proves KYC conversion and PowerCred pass rates against real
   drivers.
2. **Earnings push** — `EarningsPushEnabled`, after the §3.2 sweep-back is written and tested.
   Route the driver's share to their child wallet following `EarningsSplit.For`. Start with one
   volunteer driver. The old withdrawal path keeps working off the local mirror.
3. **Withdrawal proxy** — `WithdrawalsEnabled`. New path for migrated drivers only; unmigrated
   drivers continue on the existing handler untouched. Add PESONet here (§4).
4. ~~**Backfill.**~~ Not needed — drivers do not have full app access yet, so every driver is new.
5. **Cleanup, separate MR, much later.** Mark the §3.1 list `[Obsolete]`, let the compiler surface
   stragglers, delete only once no driver is on the old path.

## 8a. Implementation status (2026-08-30)

Branch `feat-#91-paymongo-child-wallets`. Everything below is behind flags defaulting to `false`, so
with them off behaviour is identical to today.

| Phase | Built | Notes |
| --- | --- | --- |
| 1 — onboarding | ✅ | `PayMongoAccountsClient`, 5 `DriverWallet` columns + migration, create/verify/activate handlers, 3 controller endpoints, `consumer.activated`/`declined` + `account.identity_verification.*` webhooks |
| Account fees | ✅ | ₱30 from the opening float before the account is opened; ₱15/month daily-idempotent Hangfire job |
| 2 — earnings push | ✅ | `EarningCreditConsumer` routes a migrated driver's share to their child wallet, in-network and free |
| 2 — rollback | ✅ | `PayMongoWalletSweepService` — child → parent sweep and unlink |
| 3 — rail selection | ✅ | `WithdrawalRailSelector` — InstaPay/PESONet with per-rail BIC, never a silent downgrade |
| 3 — reconciliation | ✅ | `ChildWalletWithdrawalReconciliationService` — polls transfers whose webhook never arrived |
| 3 — withdrawal proxy | ✅ | `PayMongoWithdrawalService` — reads PayMongo's balance, caps at `balance − fee`, sends from the child wallet via `Account-Id` |
| 4 — backfill | n/a | **Dropped.** Drivers do not have full app access yet, so there is no population to migrate |

**Not yet run against PayMongo.** `DriverWallet:PayMongoAccountEmailDomain` has no default and the
handler refuses to start without it, since the address is frozen onto the account permanently.

### Guards proven by mutation

Each of these was broken deliberately, the suite confirmed to fail, and the source restored:

| Guard | Test that catches its removal |
| --- | --- |
| Wallet ownership (`merchant_id` matches the requested child) | `Wallet_belonging_to_another_account_is_refused` |
| Float must cover the ₱30 before an account is opened | `Onboarding_is_refused_when_the_float_cannot_cover_the_kyc_fee` |
| Monthly fee charged at most once per calendar month | `Running_twice_in_the_same_month_does_not_double_charge` |
| Failed earnings transfer must not credit the mirror | `A_rejected_transfer_credits_nothing_and_rethrows` |
| Withdrawal must leave headroom for the fee | `Withdrawing_the_entire_balance_is_refused_with_the_real_maximum` |
| Webhook must not run reservation logic on a child withdrawal | `ChildWalletWithdrawalWebhookTests` (all three) |

The last one was not a drill: the guard was genuinely inverted in the first implementation, and a
failed transfer would have credited the driver a balance that did not exist at PayMongo.

## 9. Verification

- Unit tests for `PayMongoAccountsClient` against recorded fixtures: the `409` duplicate email, the
  `400` post-activation `PATCH`, and a `fields=balance,account` null response.
- **A test asserting that a bogus `Account-Id` on a v1 route returns the parent wallet** — lock in
  the footgun so nobody reintroduces it.
- Withdrawal cap: a request for the full balance is rejected with a fee-aware message;
  `balance − fee` succeeds.
- Rail selection: InstaPay-only institution over ₱50,000 is rejected, not silently switched;
  PESONet-only institution routes to PESONet with the correct per-rail BIC.
- One real driver through phase 1 on live before any money moves.

## 10. Open questions for PayMongo

1. **Fee rule.** Dashboard transfers came back free, API transfers charged ₱10. Channel-based, or
   one-free-per-week-per-account? And is ₱10 still current given InstaPay went free at the bank level
   in July 2026?
2. **Partner Verification.** Will they accept our Didit + InsightFace KYC in place of PowerCred,
   avoiding ₱30/driver and a second selfie? *(Per-shift InsightFace stays regardless — PayMongo has
   no equivalent; it catches account sharing after onboarding.)*
3. Does `batch_transfers` accept `provider: "auto"`? (§4.1)
4. **How does a parent deactivate a child wallet?** Wallets expose a `deactivated` status, but no
   route for setting it was found. Needed to stop the ₱15/month on inactive drivers (§6a).
5. Is the ₱30 KYC billed on account *creation* or on *activation*? Determines whether a driver who
   abandons onboarding mid-KYC still costs us.
6. Can `org_5bff545e74b5f872b1dc6818`, the probe artefact, be deleted?

## 11. Cost

| | |
| --- | --- |
| KYC | ₱30 one-time per driver |
| Wallet maintenance | ₱15/wallet/month (₱11.25 above 25k, ₱7.50 above 100k, ₱3 above 250k) |
| Cash-out | ₱10 per transfer, **borne by the driver** |
| In-network earnings push | free |

At 1,000 drivers: **₱30,000 one-time, ₱180,000/year.** The ₱15/month is additive — it does not
replace the ₱10, which a driver still pays on cash-out. It only saves money if drivers hold balance
and spend via QR Ph rather than cashing out.
