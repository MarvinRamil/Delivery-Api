# Spec — Idempotent Didit KYC session creation

**Fixes:** the HTTP 500 / "Something went wrong · CorrelationId: …" screen documented in
[`DIDIT_KYC_SESSION_ERROR.md`](./DIDIT_KYC_SESSION_ERROR.md).

**Status:** proposed
**Scope:** `bee-backend` only. No app, migration, or Didit-config changes.

---

## 1. Problem

`POST /api/kyc/session` and `POST /api/customer/kyc/session` unconditionally `INSERT` a
verification row keyed on the Didit `session_id`. Both tables have a unique index on that
column (`VerificationDbContext.cs:45` for drivers, `:66` for customers).

When a driver abandons a KYC flow before a terminal webhook lands, the Didit session stays
open. On re-engagement Didit is called with the same `vendor_data` and hands back the **same
`session_id`**, so the second insert violates the unique index. `DbUpdateException` is not
among the exceptions the controllers translate (`KycController.cs:45-53` catches only
`InvalidOperationException` → 409 and `HttpRequestException` → 503), so it reaches
`ExceptionHandlingMiddleware` and becomes a 500 with a correlation id that the app renders
verbatim.

Session reuse by Didit is the only path to a `23505` on this index: two concurrent creates
that each mint a *distinct* session id cannot collide, and the webhook insert path
(`KycService.cs:94-105`) inserts only when no row matches the session id. So the fix belongs at
the create-session insert.

## 2. Goals

- A driver or customer who re-engages an interrupted KYC flow gets the hosted URL back and
  resumes, instead of a 500.
- No duplicate rows, no lost verification state.
- The same fix in the customer flow, which has the identical shape.
- Any *other* database failure during session creation degrades to a clean, retryable
  response rather than a raw 500.

**Non-goals.** Reworking the status model, expiring stale sessions, changing the
`Approved`/`Legacy` short-circuit, or touching the webhook path.

## 3. Requirements

**R1 — Idempotent insert.** After Didit returns a session, `CreateSessionAsync` inserts a row
only if none exists for that `session_id`. If one exists, reuse it.

**R2 — Didit is still called on every request.** The hosted `url` is only obtainable from the
create-session response and is not stored, so the call cannot be skipped by short-circuiting on
a local `Pending` row. Ordering stays: guard → Didit → conditional insert.

**R3 — Reuse must not rewrite status.** An existing row may be `Pending`, `InProgress`, or
`InReview`. Reuse returns the row untouched. Resetting to `Pending` would discard progress
already recorded by a webhook or a poll reconcile.

**R4 — The insert is race-safe.** The check-then-insert in R1 is not atomic. Two concurrent
requests for the same reused session (a double-tap on *Try again*) can both find nothing and
both insert. `CreateSessionAsync` must catch `DbUpdateException`, re-query by `session_id`, and
succeed if the row now exists; only rethrow if it does not.

**R5 — Controllers translate `DbUpdateException`.** Both `CreateSession` actions gain a
`catch (DbUpdateException)` that logs and returns **503** with the existing
`"Identity verification is temporarily unavailable."` body — matching the shape the apps already
handle for a Didit outage, so no client change is needed.

**R6 — Reuse is observable.** Log at Information when an existing row is reused, distinctly
from the current "session created" line, so re-engagement rate is measurable.

## 4. Design

`KycService.CreateSessionAsync` (`Application/Services/KycService.cs:44`):

```csharp
var latest = await GetLatestAsync(userId, cancellationToken);
if (latest?.Status is DriverVerificationStatus.Approved or DriverVerificationStatus.Legacy)
    throw new InvalidOperationException("Driver identity is already verified.");

var created = await _didit.CreateSessionAsync(userId, cancellationToken);

var existing = await _db.DriverVerifications
    .FirstOrDefaultAsync(v => v.DiditSessionId == created.SessionId, cancellationToken);

if (existing is null)
{
    _db.DriverVerifications.Add(new DriverVerification
    {
        UserId = userId,
        DiditSessionId = created.SessionId,
        Status = DriverVerificationStatus.Pending
    });
    try
    {
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("[KYC] Didit session {SessionId} created for user {UserId}", created.SessionId, userId);
    }
    catch (DbUpdateException)
    {
        // R4: a concurrent request inserted the same reused session first.
        _db.ChangeTracker.Clear();
        var raced = await _db.DriverVerifications
            .FirstOrDefaultAsync(v => v.DiditSessionId == created.SessionId, cancellationToken);
        if (raced is null) throw;
        _logger.LogInformation("[KYC] Concurrent insert for session {SessionId}; reusing", created.SessionId);
    }
}
else
{
    _logger.LogInformation("[KYC] Reusing open Didit session {SessionId} for user {UserId} (status {Status})",
        created.SessionId, userId, existing.Status);
}

return new CreateKycSessionResult(created.SessionId, created.Url);
```

Notes on the choices:

- **Catch `DbUpdateException`, not `PostgresException` with `SqlState == "23505"`.** Npgsql *is*
  referenced by the module, but keeping the catch provider-agnostic avoids leaking the driver
  into the Application layer and lets the tests in §6 run on SQLite.
- **`ChangeTracker.Clear()` before the re-query** — the failed `Add` leaves the entity tracked
  in `Added` state, and any later `SaveChangesAsync` on the same scoped context would retry it.
- **Matching on `DiditSessionId` alone is sufficient.** `session_id` is globally unique in Didit.
  Cross-user or cross-table contamination is not reachable: `DiditWebhooksController.cs:85`
  routes on the `customer:` `vendor_data` prefix, so only driver sessions ever reach
  `DriverVerifications`. An optional `existing.UserId == userId` assertion is cheap defense if we
  ever want it, but it is not required for correctness today.

`CustomerKycService.CreateSessionAsync` (`Application/Services/CustomerKycService.cs:41`) takes the
identical change against `_db.CustomerVerifications`, keeping the `VendorPrefix + userId`
argument to `_didit.CreateSessionAsync`. The two services stay duplicated rather than sharing a
helper — they persist different entity types to different `DbSet`s, and the existing code already
mirrors line for line.

Controllers (`KycController.cs:49`, `CustomerKycController.cs:53`), before the
`HttpRequestException` catch:

```csharp
catch (DbUpdateException ex)
{
    _logger.LogError(ex, "[KYC] Persisting KYC session failed for user {UserId}", userId);
    return StatusCode(503, new { error = "Identity verification is temporarily unavailable." });
}
```

## 5. Behavior matrix

| Existing row for returned `session_id` | Latest status | Result |
| --- | --- | --- |
| none | none / terminal-but-not-approved | insert `Pending`, 200 + url |
| none | `Approved` / `Legacy` | 409 before Didit is called (unchanged) |
| exists | `Pending` / `InProgress` / `InReview` | reuse untouched, 200 + url |
| none, lost a concurrent insert | any | re-query, reuse, 200 + url |
| insert fails for any other reason | — | 503, correlation id in logs only |

## 6. Testing

`tests/BeeLogistics.Tests` has no EF provider package and no `DbContext`-backed tests today —
the two Didit tests (`Drivers/DiditDecisionParserTests.cs`, `Security/DiditWebhookVerifierTests.cs`)
are pure unit tests. `KycService` takes `VerificationDbContext` concretely, so `MockQueryable`
cannot exercise `SaveChangesAsync`, and **EF InMemory does not enforce unique indexes**, so it
cannot reproduce this bug at all.

Add `Microsoft.EntityFrameworkCore.Sqlite` to the test project and build the context over a
shared-cache in-memory SQLite connection, which does enforce the unique index. Then cover, per
service:

1. No existing row → one row inserted, `Pending`, result carries Didit's session id and url.
2. Didit returns a `session_id` that already has a `Pending` row → no second row, status still
   `Pending`, 200-shaped result.
3. Existing row is `InProgress` → status is still `InProgress` after the call (R3).
4. Insert races: pre-insert the row after the `FirstOrDefaultAsync` has run (fake `IDiditApiClient`
   that inserts on the way out) → `CreateSessionAsync` returns normally, one row (R4).
5. Latest is `Approved` → `InvalidOperationException`, and `_didit.CreateSessionAsync` was never
   called.

Controller-level: `DbUpdateException` from a substituted service → 503, not a rethrow.

## 7. Rollout

Single PR, no migration, no config. Deploy is safe to roll back — the change only removes an
insert that was throwing.

Verify in staging by starting a driver KYC flow, backgrounding the app before submitting the
document, then reopening and tapping the verification entry point: expect the hosted Didit page
to reopen at the same session, and an `[KYC] Reusing open Didit session …` log line.

After deploy, `23505` on `IX_DriverVerifications_DiditSessionId` /
`IX_CustomerVerifications_DiditSessionId` should disappear from logs entirely. Any remaining
occurrence means an insert path other than `CreateSessionAsync` — i.e. `ProcessWebhookAsync` —
and is a separate bug.

## 8. Acceptance criteria

- [ ] Re-engaging an interrupted KYC session returns 200 with a usable hosted url, for both driver and customer.
- [ ] No duplicate rows; no status regression on reuse.
- [ ] `DbUpdateException` never escapes either `CreateSession` action.
- [ ] Tests in §6 pass, including the race case.
- [ ] `DIDIT_KYC_SESSION_ERROR.md` gets a note at the top pointing here and marking the bug fixed.
