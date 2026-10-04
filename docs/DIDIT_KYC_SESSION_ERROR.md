# Driver KYC — "Something went wrong · CorrelationId: …" explained

This documents the error a driver sees during identity verification:

> **Something went wrong**
> An error occurred. CorrelationId: `60efba5d…`
> [ Try again ]

It looks like a Didit error, but it is **not**. It is a plain **HTTP 500 from our own
backend**, rendered by the driver app's KYC error screen. This doc traces exactly how it
happens, states the **confirmed root cause**, and gives the fix.

---

## TL;DR

- The screen is **our app** (`bee-driver`), not Didit's hosted page.
- It fires when **`POST /api/kyc/session`** returns **500** — *before* the Didit flow re-opens.
- **Confirmed root cause:** a duplicate-key violation inserting a `DriverVerification` row —
  the driver **re-engaged a disrupted session**, Didit returned the **same `session_id`**, and
  we tried to `INSERT` a second row with a `DiditSessionId` that already exists.
- The `CorrelationId` is **our** backend trace id (`ExceptionHandlingMiddleware`), not Didit's.
- Fix: make the insert **idempotent** (reuse the existing row instead of inserting a duplicate).

---

## The confirmed exception

```
Microsoft.EntityFrameworkCore.DbUpdateException: An error occurred while saving the entity changes.
 ---> Npgsql.PostgresException (0x80004005): 23505: duplicate key value violates unique
      constraint "IX_DriverVerifications_DiditSessionId"
    SqlState:       23505
    SchemaName:     verification
    TableName:      DriverVerifications
    ConstraintName: IX_DriverVerifications_DiditSessionId
```

`23505` is Postgres for "unique violation." The unique index is created by the
`InitialVerification` migration — one row per Didit `session_id`, no duplicates allowed.

---

## Why the duplicate happens (the "how")

Each call to `POST /api/kyc/session` runs `KycService.CreateSessionAsync`, which
**unconditionally inserts a new row**:

```csharp
var latest = await GetLatestAsync(userId, ct);
if (latest?.Status is Approved or Legacy) throw new InvalidOperationException("…already verified.");

var created = await _didit.CreateSessionAsync(userId, ct);   // Didit returns a session_id + url

_db.DriverVerifications.Add(new DriverVerification            // ← always an INSERT
{
    UserId = userId,
    DiditSessionId = created.SessionId,
    Status = DriverVerificationStatus.Pending
});
await _db.SaveChangesAsync(ct);                               // ← 23505 here on re-engagement
```

The trigger is **session re-engagement**:

1. Driver starts KYC → we create Didit **session A**, insert a `Pending` row with
   `DiditSessionId = A`. Flow opens in the WebView.
2. The flow is **disrupted** — app backgrounded/closed, network drop, driver taps back — before a
   terminal webhook lands. Session A stays open (`Not Started` / `In Progress`) and the local row
   stays `Pending`.
3. Driver re-engages → `POST /api/kyc/session` again. Didit is called with the **same
   `vendor_data` (the userId)** and, because session A is still open, **returns session A again**
   (same `session_id`) instead of minting a new one.
4. We try to `INSERT` a *second* row with `DiditSessionId = A` → the unique index
   `IX_DriverVerifications_DiditSessionId` rejects it → `DbUpdateException (23505)`.

The `GetLatestAsync` guard at the top only short-circuits **already-verified** drivers
(`Approved`/`Legacy`); a `Pending`/`InProgress` row falls straight through to the duplicate insert.

---

## How the 500 becomes "Something went wrong"

`KycController.CreateSession` only translates two exception types:

```csharp
catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); } // 409
catch (HttpRequestException ex)      { return StatusCode(503, …); }                   // 503
// DbUpdateException is NOT caught → escapes to the global handler
```

Because `DbUpdateException` is neither of those, it propagates to
**`ExceptionHandlingMiddleware`**, which:

1. Logs `Unhandled exception. CorrelationId: {id}` (with the stack trace above).
2. Returns HTTP **500** with body `ApiResponse.Fail("An error occurred. CorrelationId: {id}")`.

> The exact text `An error occurred. CorrelationId:` is the **Development** branch of the
> handler. In Production the same failure reads `An unexpected error occurred. Reference: {id}`.

The app then renders it verbatim: `kycService.createSession()` rethrows `new Error(message)` and
`kyc-verification.tsx` shows it under the fixed title **"Something went wrong."** The
`[API] Parsed Error Data: {"success":false…}` toast is our `apiClient` parsing that body.

```
bee-driver KYC screen ──POST /api/kyc/session──► KycController
                                                    └─ KycService.CreateSessionAsync
                                                         └─ INSERT DriverVerification  ✗ 23505
                                                    ↑ DbUpdateException (uncaught)
ExceptionHandlingMiddleware → 500 { success:false, message:"…CorrelationId: <id>" }
   ↑ rendered as "Something went wrong"
```

---

## The fix

Make session creation **idempotent**: after Didit returns the session, only insert if we don't
already have a row for that `session_id`. If Didit handed back an existing session, reuse the
row and return the fresh URL so the driver simply re-opens the same session.

```csharp
var created = await _didit.CreateSessionAsync(userId, ct);

var existing = await _db.DriverVerifications
    .FirstOrDefaultAsync(v => v.DiditSessionId == created.SessionId, ct);

if (existing is null)
{
    _db.DriverVerifications.Add(new DriverVerification
    {
        UserId = userId,
        DiditSessionId = created.SessionId,
        Status = DriverVerificationStatus.Pending
    });
    await _db.SaveChangesAsync(ct);
}

return new CreateKycSessionResult(created.SessionId, created.Url);
```

`session_id` is globally unique in Didit, so matching on `DiditSessionId` alone is safe — an
existing row for that id always belongs to this same user.

**Defense in depth** (recommended alongside the idempotent insert): also catch `DbUpdateException`
in `KycController.CreateSession` and return a clean **503**/retry response, so any *other* future
constraint issue degrades gracefully instead of surfacing a raw 500.

> **Apply the same fix to the customer flow.** `CustomerKycService.CreateSessionAsync`
> (`…/Application/Services/CustomerKycService.cs`) and `CustomerKycController` have the identical
> unconditional-insert pattern and unique index (`IX_CustomerVerifications_DiditSessionId`), so
> they hit the same bug on re-engagement.

---

## How to confirm on any future report

The `CorrelationId` on the screen maps 1:1 to the backend log line
`Unhandled exception. CorrelationId: <id>`. For this bug the inner exception is:

```
Npgsql.PostgresException 23505: duplicate key value violates unique constraint
"IX_DriverVerifications_DiditSessionId"
```

Any other inner exception (e.g. `TaskCanceledException` for a Didit timeout,
`KeyNotFoundException`/`JsonException` for an unexpected Didit body) points to a different cause.

---

## Related files

| Concern                     | File                                                                                  |
| --------------------------- | ------------------------------------------------------------------------------------- |
| Driver KYC screen (UI)      | `bee-driver/app/kyc-verification.tsx`                                                  |
| App API wrapper             | `bee-driver/features/kyc/services/kycService.ts`                                       |
| Endpoint                    | `…/Modules/BeeLogistics.Modules.Verification/Presentation/Controllers/KycController.cs`|
| Session logic (the bug)     | `…/Modules/BeeLogistics.Modules.Verification/Application/Services/KycService.cs`       |
| Customer mirror (same bug)  | `…/Modules/BeeLogistics.Modules.Verification/Application/Services/CustomerKycService.cs`|
| Didit HTTP client           | `…/Modules/BeeLogistics.Modules.Verification/Infrastructure/DiditApiClient.cs`         |
| 500 + CorrelationId handler | `src/BeeLogistics.Api/Middleware/ExceptionHandlingMiddleware.cs`                       |
| Unique index (migration)    | `…/Infrastructure/Migrations/20260706154455_InitialVerification.cs`                    |
| Setup / config              | `bee-backend/docs/DIDIT_KYC_SETUP.md`                                                  |
