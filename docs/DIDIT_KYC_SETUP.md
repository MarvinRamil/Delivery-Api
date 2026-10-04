# Didit KYC — Setup Guide

Didit is the **driver onboarding identity check** (ID document scan + selfie + liveness + face
match). It is a hosted third-party service: the driver app opens a Didit-hosted URL in a WebView,
the driver completes the flow, and Didit calls our webhook with the result.

- **Scope:** driver app only. The customer app is not involved.
- **Independent from FaceMatch / ShiftCheck** (the per-shift face check). Didit works standalone —
  see [Relationship to the shift face check](#relationship-to-the-shift-face-check).
- **Module:** `BeeLogistics.Modules.Verification`.

---

## 1. Get credentials from the Didit console

Sign up at <https://business.didit.me> (free tier: 500 sessions/month), then create a
**verification workflow** (ID + selfie + liveness + face match). Collect three values:

| Value              | Config key            | Notes                                        |
| ------------------ | --------------------- | -------------------------------------------- |
| **API Key**        | `Didit:ApiKey`        | Server-side secret, sent as `x-api-key`.     |
| **Workflow ID**    | `Didit:WorkflowId`    | Which verification flow to run.              |
| **Webhook Secret** | `Didit:WebhookSecret` | HMAC secret used to verify incoming webhooks.|

---

## 2. Configure the backend

Set these via environment variables (or `.env` / docker-compose — keys already exist in
`.env.example`). **Do not** put secrets in `appsettings.json`; leave those blank and inject via env,
matching the rest of the app.

```env
DIDIT_ENABLED=true
DIDIT_API_KEY=<from console>
DIDIT_WORKFLOW_ID=<from console>
DIDIT_WEBHOOK_SECRET=<from console>
# DIDIT_CALLBACK_URL=beeapp://kyc-callback   # default is fine (brand-neutral, shared by both apps)
```

These map to the `Didit` section (see `DiditOptions.cs`). The integration is only active when
**all three** of `Enabled`, `ApiKey`, and `WorkflowId` are set — otherwise `DiditApiClient.Enabled`
is `false` and `POST /api/kyc/session` returns **503**.

Full option reference:

| Env var                     | Config key                     | Default                          |
| --------------------------- | ------------------------------ | -------------------------------- |
| `DIDIT_ENABLED`             | `Didit:Enabled`                | `false`                          |
| `DIDIT_API_KEY`             | `Didit:ApiKey`                 | `""`                             |
| `DIDIT_WORKFLOW_ID`         | `Didit:WorkflowId`             | `""`                             |
| `DIDIT_WEBHOOK_SECRET`      | `Didit:WebhookSecret`          | `""`                             |
| `DIDIT_CALLBACK_URL`        | `Didit:CallbackUrl`            | `beeapp://kyc-callback`          |
| —                           | `Didit:BaseUrl`                | `https://verification.didit.me`  |
| —                           | `Didit:TimeoutSeconds`         | `30`                             |
| —                           | `Didit:WebhookToleranceSeconds`| `300`                            |

---

## 3. Configure the webhook (required)

This is how verification results come back. In the Didit console, set the webhook URL to:

```
https://<your-api-host>/api/webhooks/didit
```

- Must be **publicly reachable over HTTPS** (Didit calls it from the internet).
- Authenticated with HMAC (`X-Signature` + `X-Timestamp`) using `DIDIT_WEBHOOK_SECRET`. If the
  secret is not set, the endpoint rejects every request with **401**.
- The secret in the console must match `DIDIT_WEBHOOK_SECRET`.
- Didit retries on 5xx/404 with backoff; the timestamp tolerance is `WebhookToleranceSeconds`
  (default 300s) for replay protection.

---

## 4. Supporting pieces (already in place)

- **App deep link** — `Didit:CallbackUrl` is `beeapp://kyc-callback`, a brand-neutral sentinel. It
  does **not** need to match either app's OS scheme (`beedriversapp` / `beecustomerapp`): the KYC
  screen intercepts it inside the WebView (`onShouldStartLoadWithRequest` → `startsWith`) before it
  ever reaches the OS. Both the driver and customer apps can therefore reuse the same callback.
- **File storage** — approved reference selfies are stored to S3/R2 in the `driver-verifications`
  bucket; the existing `S3` config is used.
- **Database** — run migrations on deploy; `InitialVerification` creates the Verification DB
  (`__VerificationMigrationsHistory`).

---

## Flow

1. Driver app calls `POST /api/kyc/session` → backend calls Didit `POST v3/session/` and returns the
   hosted `verificationUrl`.
2. App opens the URL in a WebView; on finish Didit redirects to `beeapp://kyc-callback` (a
   brand-neutral sentinel both apps intercept in-WebView — see the deep-link note below).
3. Didit calls `POST /api/webhooks/didit`. The backend re-fetches the decision from Didit (media
   URLs in the webhook are short-lived), stores scores + the reference selfie, and marks the driver
   verified.
4. `GET /api/kyc/status` returns the current status and also poll-reconciles pending sessions as a
   webhook fallback.

**Statuses:** `NotStarted`, `Pending`, `InProgress`, `InReview`, `Approved`, `Declined`,
`Abandoned`, `Expired`, `Legacy`.

---

## Go-live checklist

- [ ] Create Didit workflow; copy API Key, Workflow ID, Webhook Secret.
- [ ] Set `DIDIT_ENABLED=true` + the three secrets in the deploy environment.
- [ ] Configure the webhook URL `https://<api-host>/api/webhooks/didit` in the Didit console.
- [ ] Confirm the API host is publicly reachable over HTTPS.
- [ ] Run EF migrations (Verification DB).
- [ ] Smoke test: `POST /api/kyc/session` returns a URL; complete a session; confirm the webhook
      lands and `GET /api/kyc/status` shows `Approved`.

---

## Relationship to the shift face check

Didit and the per-shift face check are **separate systems** that share only one thing.

|            | **Didit KYC**                      | **Shift face check (FaceMatch)**            |
| ---------- | ---------------------------------- | ------------------------------------------- |
| Provider   | Didit (external hosted SaaS)       | Self-hosted InsightFace (`docker/facematch`)|
| When       | Once, at onboarding                | Every shift, before going online            |
| Purpose    | Prove identity vs. ID              | Prove it's the same person each shift       |
| Config     | `Didit`                            | `FaceMatch` + `ShiftCheck`                   |

The only link: on Didit approval the backend stores the reference selfie/embedding, which the shift
check later compares against. **Didit does not require FaceMatch/ShiftCheck** — you can (and by
default do) run Didit with the shift check disabled. See `FaceMatchOptions.cs` / `ShiftCheckOptions.cs`
for that side.

## Not required for Didit

- FaceMatch container and `ShiftCheck` gate — leave disabled.
- Any customer-app changes — driver-only.
