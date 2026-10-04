# Driver Verification — Flow Architecture (KYC, Face, Shift)

End-to-end architecture for the three driver verification flows, all in
`BeeLogistics.Modules.Verification`:

1. **KYC onboarding** — identity proof via **Didit** (external SaaS).
2. **Reference selfie / embedding** — the bridge created at KYC approval.
3. **Per-shift face check** — "still the same person?" via **self-hosted FaceMatch + Liveness**.

> **Key fact:** the shift face check is **NOT Didit.** Didit runs only at onboarding. The shift check
> uses self-hosted services (InsightFace + YOLO). They share exactly one thing: the reference selfie
> Didit produced, stored at KYC approval and re-used by every shift check.

---

## Services at a glance

| Service            | Type                     | Used by            | Config section |
| ------------------ | ------------------------ | ------------------ | -------------- |
| **Didit**          | External hosted SaaS     | KYC onboarding     | `Didit`        |
| **FaceMatch**      | Self-hosted InsightFace  | Shift check + KYC embedding | `FaceMatch` |
| **Liveness**       | Self-hosted YOLO anti-spoof | Shift check (optional stage) | `Liveness` |
| **File storage**   | S3 / R2                  | Reference selfie storage | `S3`     |

---

## Flow 1 — KYC onboarding (Didit)

```
 Driver app                 Backend (Verification)              Didit (external)
 ──────────                 ──────────────────────              ────────────────
     │  POST /api/kyc/session      │                                  │
     ├────────────────────────────>│  CreateSessionAsync              │
     │                             │   POST v3/session/  ─────────────>│  (workflow_id,
     │                             │                                  │   vendor_data=userId,
     │                             │<───────────  session_id + url ────┤   callback)
     │                             │  save DriverVerification(Pending) │
     │<──── verificationUrl ───────┤                                  │
     │                             │                                  │
     │  open url in WebView        │                                  │
     ├──────────────  ID + selfie + liveness + face match  ──────────>│
     │  redirect beeapp://kyc-callback (intercepted in-WebView)       │
     │                             │                                  │
     │                             │<──── POST /api/webhooks/didit ────┤  (HMAC signed)
     │                             │  verify HMAC (X-Signature/-Timestamp)
     │                             │  re-fetch decision (v3/session/{id}/decision)
     │                             │  store scores + reference selfie  │
     │                             │  status → Approved / Declined     │
     │  GET /api/kyc/status  ──────>│  (also poll-reconciles pending)  │
     │<──── status ────────────────┤                                  │
```

**Endpoints**
- `POST /api/kyc/session` — start; returns hosted `verificationUrl` (503 if Didit not configured).
- `GET  /api/kyc/status` — current status; polls Didit as a webhook fallback.
- `POST /api/webhooks/didit` — Didit → backend, HMAC-authenticated (`AllowAnonymous` + signature).

**Key code**
- `KycController`, `KycService`
- `DiditApiClient` / `DiditOptions` / `IDiditApiClient`
- `DiditDecisionParser`, `DiditWebhookVerifier`, `DiditWebhooksController`

**Why the webhook re-fetches the decision:** media URLs in the webhook payload are short-lived, and
re-fetching from the API guards against a forged/partial payload.

**Statuses:** `NotStarted → Pending → InProgress → InReview → Approved | Declined | Abandoned | Expired`
(`Legacy` = verified before Didit existed).

---

## Flow 2 — Reference selfie & embedding (the bridge)

This is what connects Didit to the shift check. It happens **inside** KYC approval
(`KycService.ApplyDecisionAsync` → `StoreReferenceSelfieAsync`).

```
 On Didit "Approved":
   1. Pick media URL: live selfie  (preferred) else ID portrait
   2. Download from Didit (presigned https)
   3. Upload to S3/R2  →  bucket "driver-verifications"
        stored on DriverVerification.ReferenceSelfiePath  ("s3:bucket:objectKey")
   4. If FaceMatch enabled:  EmbedAsync(selfie)  →  face embedding (float[])
        stored on DriverVerification.ReferenceEmbedding  (JSON)
```

- The embedding is computed **eagerly** here only if FaceMatch is enabled at KYC time.
- If FaceMatch was off / down, the embedding is **backfilled lazily** on the first shift check
  (`ShiftCheckService.EnsureReferenceEmbeddingAsync` → downloads the stored selfie → `EmbedAsync`).
- Legacy drivers have neither → shift check skips them.

**This is the ONLY coupling between Didit and the shift check.**

---

## Flow 3 — Per-shift face check (FaceMatch + Liveness — NOT Didit)

Gates a driver going online. Enforced only when `ShiftCheck:Enabled && FaceMatch:Enabled` and not
in `ShadowMode`.

### 3a. The gate (going online)

```
 Driver app                         Backend
 ──────────                         ───────
   PATCH /api/users/driver/status {isOnline:true}
       │
       ├─> IShiftCheckGate.RequiresCheckAsync(userId, lastFaceCheckAt)
       │       • disabled / shadow / no FaceMatch      → false (allow)
       │       • lastFaceCheckAt < MaxAgeHours (12h)    → false (allow)
       │       • no reference selfie (legacy driver)    → false (allow)
       │       • else                                   → true  (block)
       │
       ├─ true  → 4xx "complete a quick face check"  → app opens /shift-check (Flow 3b)
       └─ false → IsOnline = true  (driver goes online)
```

### 3b. The face check itself

```
 Driver app                    Backend (ShiftCheckService)     Self-hosted services
 ──────────                    ───────────────────────────     ────────────────────
   POST /api/shift-check/session
       ├──────────────────────────>│ EnsureReferenceEmbedding (backfill if needed)
       │<─── sessionId + directions │   (random head-pose sequence, N = DirectionCount)
       │                            │
   for each direction:             │
   POST /api/shift-check/session/{id}/submit  (image + direction)
       ├──────────────────────────>│  (optional) Liveness.CheckLiveness ──> YOLO anti-spoof
       │                            │  FaceMatch.Verify(frame, refEmbedding) ─> InsightFace
       │                            │    score >= MatchThreshold (0.45) ? pass : fail
       │                            │  record ShiftFaceCheck (score, passed)
       │<─── pass/fail + remaining ─┤
       │                            │
   when all directions pass:       │  IOnShiftCheckPassed.MarkPassedAsync(userId)
                                    │    → Identity: ApplicationUser.LastFaceCheckAt = now
       retry PATCH driver/status ─> gate now passes → IsOnline = true
```

**Endpoints**
- `POST /api/shift-check/session` — start; returns `sessionId` + head-pose directions.
- `POST /api/shift-check/session/{sessionId}/submit` — submit one frame for one direction.
- `GET  /api/shift-check/session/{sessionId}` — session status.
- `PATCH /api/users/driver/status` — the online toggle that the gate hooks into (Identity module).

**Key code**
- `ShiftCheckController`, `ShiftCheckService` (implements `IShiftCheckService` + `IShiftCheckGate`)
- `FaceMatchApiClient` (`/v1/embed`, `/v1/verify`), `LivenessApiClient`
- `IOnShiftCheckPassed` → `ShiftCheckPassedHandler` (Identity) stamps `LastFaceCheckAt`
- `UsersController.UpdateDriverOnlineStatus` — the gate call site

**Resilience**
- Liveness service down → degrade to face-match-only (don't block drivers).
- `ShadowMode` → run + log scores but never block (used to calibrate `MatchThreshold`).
- Passed check valid for `MaxAgeHours` (12h) before re-verification is required.

---

## Cross-module write-backs (Verification → Identity)

The Verification module owns the checks but not the user record. It writes back via two optional,
null-safe interfaces (same pattern as `IOnLivenessVerified`):

| Event                 | Interface             | Handler (Identity)        | Effect                                  |
| --------------------- | --------------------- | ------------------------- | --------------------------------------- |
| KYC approved          | `IOnLivenessVerified` | `LivenessVerifiedHandler` | marks driver identity-verified          |
| Shift check passed    | `IOnShiftCheckPassed` | `ShiftCheckPassedHandler` | sets `ApplicationUser.LastFaceCheckAt`  |

Both are registered as optional (`= null`) — if unwired, verification still runs, it just doesn't
stamp the user.

---

## Persistence

- `DriverVerification` — one row per KYC attempt: `Status`, scores, `ReferenceSelfiePath`,
  `ReferenceEmbedding`, ID fields. (Verification DB)
- `ShiftFaceCheck` — one row per shift-check attempt: `MatchScore`, `Passed`. (Verification DB)
- `ApplicationUser.LastFaceCheckAt` + `IsOnline` — (Identity DB)
- Migrations: `InitialVerification` (Verification DB), `AddLastFaceCheckAt` (Identity DB).

---

## Enable / disable matrix

| Want                                   | `Didit:Enabled` | `FaceMatch:Enabled` | `ShiftCheck:Enabled` |
| -------------------------------------- | :-------------: | :-----------------: | :------------------: |
| Nothing (current default)              | false           | false               | false                |
| **KYC onboarding only**                | **true**        | false               | false                |
| KYC + calibrate shift check (log only) | true            | true                | true + `ShadowMode`  |
| KYC + enforce per-shift face check     | true            | true                | true                 |

Notes:
- Shift check requires `FaceMatch:Enabled` (the gate returns "allow" if FaceMatch is off).
- FaceMatch also improves KYC by pre-computing the reference embedding, but is not required for KYC.
- See `DIDIT_KYC_SETUP.md` for Didit credentials + webhook setup.

---

## Which app does what

- **bee-driver** — both flows: `app/kyc-verification.tsx` (Didit WebView),
  `app/shift-check.tsx` + `features/liveness/HeadPoseCapture` (shift check).
  Uses `beedriversapp://` deep link for the Didit callback.
- **bee-customer** — not involved.
- **back-office** — consumes KYC outcomes for driver approval review; does not run either flow.
