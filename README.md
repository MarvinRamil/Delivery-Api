# Bee Logistics Backend API

A logistics management platform built with .NET 10, structured as a modular monolith covering
bookings, drivers, payments, verification and customer relationships.

## Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Technology Stack](#technology-stack)
- [Modules](#modules)
- [Getting Started](#getting-started)
- [Configuration](#configuration)
- [API Documentation](#api-documentation)
- [Database Structure](#database-structure)
- [Security](#security)
- [Development Guidelines](#development-guidelines)
- [Contributing: Issues, Branches and Merge Requests](#contributing-issues-branches-and-merge-requests)
- [CI/CD Pipeline](#cicd-pipeline)
- [Docker Deployment](#docker-deployment)

## Overview

Bee Logistics is an on-demand delivery platform. The backend serves the customer app, the driver
app and the back-office, and covers:

- **Bookings**: multi-stop bookings, vehicle pricing, proof of delivery, tips
- **Drivers**: applications, wallet and withdrawals, missions, offers
- **Verification**: customer and driver KYC, per-shift face checks, liveness
- **Payments**: PayMongo and Xendit, saved payment methods, refunds
- **Money movement**: revenue splits, platform commission, double-entry ledger
- **Growth**: referrals and points, giveaways and campaigns, ratings
- **Trust & safety**: fraud signals and rules
- **CRM**: support tickets (Zammad-backed) and FAQs
- **Realtime**: chat, notifications and driver location via SignalR and MQTT

The application follows a **modular monolith** architecture: each business domain is a separate
project with its own `DbContext`, migrations and API surface, composed into one ASP.NET Core host.

## Architecture

### Modular Monolith Pattern

Every module under `src/Modules/BeeLogistics.Modules.*` has the same four-layer shape:

```
ModuleName/
├── Domain/          # Entities, enums, domain logic
├── Application/     # Handlers, Validators, DTOs, Interfaces, Services, Consumers
├── Infrastructure/  # <Name>DbContext, Repositories, Services, Migrations
└── Presentation/    # Controllers, Hubs
```

Modules do not reach into each other's `DbContext`. They communicate through contracts in
`BeeLogistics.Shared/Contracts` or asynchronously via MassTransit messages.

### Key Architectural Patterns

- **CQRS with MediatR**: controllers stay thin and `Send()` a command or query; logic lives in
  `Application/Handlers`
- **Result pattern**: `Result` / `Result<T>` with a `ResultErrorKind` (`Failure` → 400,
  `NotFound` → 404, `Forbidden` → 403) so handlers report failure without throwing
- **Repository pattern**: data access behind `IRepository`
- **Validation**: FluentValidation validators applied automatically by `ValidationBehavior`
- **Multi-tenancy**: tenant-based data isolation using `TenantId`
- **Event-driven integration**: MassTransit consumers in `Application/Consumers`

### Shared Components

`BeeLogistics.Shared` holds the cross-cutting pieces:

- `Entity` / `IEntity` base classes and `Result<T>`
- `UserRoles` constants — prefer these over role string literals
- Abstractions for caching, file storage, encryption, image processing and virus scanning
- `ValidationBehavior` MediatR pipeline behavior
- SignalR hubs and `BaseController` response handling

## Technology Stack

| Area | Technology |
|---|---|
| Runtime | .NET 10.0, ASP.NET Core |
| Data | Entity Framework Core 10, PostgreSQL, Npgsql |
| Cache | Redis (StackExchange) |
| Messaging | MassTransit + RabbitMQ |
| Realtime | SignalR (Redis backplane), MQTT for driver location |
| Background jobs | Hangfire (PostgreSQL storage) |
| Requests | MediatR (CQRS), FluentValidation |
| Auth | JWT bearer — Clerk (RS256 via JWKS) plus a legacy HS256 scheme |
| Secrets | HashiCorp Vault (KV v2) via VaultSharp |
| Payments | PayMongo, Xendit |
| Storage | MinIO / S3-compatible object storage, ImageSharp, ClamAV (nClam) virus scanning |
| Observability | Serilog (+ Seq), OpenTelemetry (OTLP + Prometheus) |
| API docs | Scalar, Swashbuckle |

## Modules

Eighteen modules, each owning its own schema and migrations.

| Module | Purpose | Controllers |
|---|---|---|
| **Identity** | Authentication, users, roles, audit logs | `AuthController`, `LoginV2Controller`, `PhoneAuthController`, `UsersController`, `ClerkWebhookController`, `AuditLogsController` |
| **Bookings** | Bookings, customers, driver offers, vehicle pricing | `BookingsController`, `CustomersController`, `DriverOffersController`, `VehiclePricingController` |
| **Drivers** | Driver applications, wallet, withdrawals, missions | `DriverApplicationsController`, `DriverWalletController`, `SavedWithdrawalMethodsController`, `AdminMissionsController` |
| **Verification** | Customer and driver KYC, liveness, per-shift face checks | `KycController`, `CustomerKycController`, `LivenessController`, `ShiftCheckController`, `DiditWebhooksController` |
| **Payment** | Payment processing, saved methods, provider webhooks | `PaymentsController`, `SavedPaymentMethodsController`, `PayMongoWebhooksController`, `WebhooksController` |
| **Revenue** | Earnings splits and platform commission | *(no HTTP surface — consumed internally)* |
| **Accounting** | Double-entry ledger, account codes, sales entries | `LedgerController` |
| **Offers** | Offer lifecycle | `OffersController` |
| **Rating** | Customer and driver ratings | `RatingsController` |
| **Referrals** | Referral codes, referrals, user points | `ReferralsController` |
| **Giveaways** | Campaigns, giveaways, entries, prizes, winners | `CampaignsController`, `GiveawaysController` |
| **Fraud** | Fraud events, signals and rules | `FraudController` |
| **CRM** | Support tickets (Zammad) and FAQs | `TicketsController`, `FaqController`, `ZammadWebhooksController` |
| **Chat** | Direct, group, support and dispatch conversations | `ChatController` + `ChatHub` |
| **Notification** | Push, SMS and email delivery and status | `DeviceTokensController`, `PushStatusController`, `SmsStatusController`, `EmailStatusController` + `NotificationHub` |
| **Map** | Driver location tracking, geocoding, MQTT credentials | `LocationsController`, `MqttCredentialsController` + `LocationHub` |
| **Company** | Company/tenant management | `CompaniesController` |
| **Integration** | Outbound integrations (e.g. back-office webhooks) | *(no HTTP surface — MassTransit consumers)* |

### Roles

Defined in `BeeLogistics.Shared/Abstractions/UserRoles.cs`:

`SuperAdmin`, `Admin`, `Owner`, `Dispatcher`, `Driver`, `Customer`, `BusinessClient`

Endpoints are gated with `[Authorize]`, `[Authorize(Roles = ...)]` or a named policy such as
`"Backoffice"`.

### Booking Statuses

`Pending` → `Confirmed` → `DriverAssigned` → `PickedUp` → `InTransit` → `Completed`, plus
`Cancelled`. Older names (`Dispatched`, `OnTheWayToPickup`, `InProgress`, `Delivered`) are
`[Obsolete]` aliases kept for backward compatibility — do not use them in new code.

## Getting Started

### Prerequisites

- .NET 10.0 SDK
- PostgreSQL 16+
- Redis 7+
- RabbitMQ
- Access to a HashiCorp Vault instance (the app **fails fast at startup** if Vault is unreachable)
- Docker and Docker Compose (for containerized development — see `README.DEV.md`)

### Local Development Setup

For the fully containerized path with hot reload, follow **`README.DEV.md`**. To run against the
SDK directly:

1. **Clone the repository**
   ```bash
   git clone https://gitlab.ilocosscript.live/enzo.delacruz/bee-backend.git
   cd bee-backend
   ```

2. **Configure Vault access and connection strings** in
   `src/BeeLogistics.Api/appsettings.Development.json` (see [Configuration](#configuration)).

3. **Restore dependencies**
   ```bash
   dotnet restore
   ```

4. **Run database migrations** — each module migrates separately:
   ```bash
   dotnet ef database update \
     --project src/Modules/BeeLogistics.Modules.Identity \
     --startup-project src/BeeLogistics.Api
   ```
   On first run `DbSeeder` applies the remaining contexts. If you add a module, remember to
   register its context in `DbSeeder.cs` or its migrations will silently never run.

5. **Run the application**
   ```bash
   dotnet run --project src/BeeLogistics.Api
   ```

6. **Access the API**
   - HTTP: `http://localhost:5248`
   - HTTPS: `https://localhost:7201`
   - API documentation: `/scalar` (Scalar) and `/swagger` — both gated behind the Swagger toggle,
     which is off outside development

## Configuration

### Secrets come from Vault, not appsettings

Application secrets live in HashiCorp Vault (KV v2, secret `bee/config`) and are loaded at startup
as the **highest-precedence** configuration source, overriding both files and environment
variables. The container authenticates with an AppRole; it never receives the secrets themselves.

Only the Vault coordinates are supplied as environment variables / CI variables:

| Variable | Purpose |
|---|---|
| `VAULT_ADDRESS` | Vault endpoint |
| `VAULT_ROLE_ID` | AppRole role_id, scoped per environment |
| `VAULT_SECRET_ID` | AppRole secret_id, scoped per environment |
| `VAULT_SKIP_TLS_VERIFY` | `true` for dev/staging self-signed certs |

Everything else — `POSTGRES_*`, `JWT_SECRET`, the PayMongo and Xendit keys, S3/DigitalOcean keys,
MQTT and SMS credentials, `ZAMMAD_API_TOKEN`, `SEED_DEFAULT_PASSWORD` — is read from Vault.
**Do not add secrets to `appsettings.json`, `.env` or CI variables.**

Each environment's Vault `bee/config` must hold that environment's own values (its own database,
Redis and RabbitMQ credentials).

### Development override

In the `Development` environment only, selected sections of local `appsettings` win over the Vault
overlay, so you can work against sandbox keys without touching Vault. Database, RabbitMQ and
encryption keys still come from Vault. The overridden sections and the reasoning are documented
inline in `src/BeeLogistics.Api/Program.cs`.

### Non-secret settings

Non-sensitive values (JWT issuer/audience/expiry, CORS origins, Serilog sinks, feature toggles) stay
in `appsettings.json` and are set per environment in `.gitlab-ci.yml`.

## API Documentation

### Base URL

- Development: `http://localhost:5248/api`
- Dev environment: `https://dev-api.bee-app.tech/api`

### Authentication

Most endpoints require a JWT bearer token:

```
Authorization: Bearer <your-jwt-token>
```

Two schemes are wired. Clerk-issued tokens are validated as RS256 against JWKS discovered from the
Clerk authority; the legacy HS256 scheme remains for existing clients, and the scheme is selected
per request. Service-to-service callers authenticate with hashed API keys configured under
`ServiceClients`.

### Response Format

**Success**:
```json
{
  "success": true,
  "message": "Operation completed successfully",
  "data": { }
}
```

**Error**:
```json
{
  "success": false,
  "message": "Error message",
  "errors": ["Error detail 1", "Error detail 2"]
}
```

### Pagination

Paginated endpoints accept `page` (default 1) and `pageSize` (default 10), and respond with:

```json
{
  "items": [],
  "totalCount": 100,
  "page": 1,
  "pageSize": 10,
  "totalPages": 10,
  "hasNextPage": true,
  "hasPreviousPage": false
}
```

### SignalR Hubs

- `/hubs/notifications` — real-time notifications
- `/hubs/chat` — real-time chat
- `/hubs/location` — driver location updates

```javascript
const connection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/notifications", { accessTokenFactory: () => token })
  .build();
```

Driver location is also ingested over MQTT (`<env>/beelogistics/drivers/+/location`).

## Database Structure

### One context per module

Each module owns its `DbContext` and its own `Migrations/` folder, which allows independent schema
evolution and keeps module boundaries enforceable:

`IdentityAppDbContext`, `BookingsDbContext`, `DriversDbContext`, `VerificationDbContext`,
`PaymentDbContext`, `RevenueDbContext`, `AccountingDbContext`, `OffersDbContext`,
`RatingDbContext`, `ReferralsDbContext`, `GiveawaysDbContext`, `FraudDbContext`, `CrmDbContext`,
`ChatDbContext`, `NotificationDbContext`, `MapDbContext`, `CompanyDbContext`

### Entity Base Classes

- **Entity**: base entity with `Id`, `TenantId`, `CreatedAt`, `UpdatedAt`
- **TenantEntity**: entity that must belong to a tenant (non-nullable `TenantId`)

### Migrations

```bash
# Create a migration (in the module that owns the entities)
dotnet ef migrations add MigrationName \
  --project src/Modules/BeeLogistics.Modules.Identity \
  --startup-project src/BeeLogistics.Api

# Apply it
dotnet ef database update \
  --project src/Modules/BeeLogistics.Modules.Identity \
  --startup-project src/BeeLogistics.Api
```

A schema change without a matching migration in that module is a review blocker.

## Security

### Authentication & Authorization

- JWT bearer authentication (Clerk RS256 + legacy HS256)
- Role-based access control via `UserRoles`
- Policy-based authorization for back-office surfaces
- Hashed service-to-service API keys

### Security Headers

`SecurityHeadersMiddleware` applies the OWASP set: Content-Security-Policy,
`X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `X-XSS-Protection`, `Referrer-Policy`,
`Permissions-Policy`, and cache control for sensitive responses.

### Input Validation

- FluentValidation on all inputs, applied by `ValidationBehavior` in the MediatR pipeline
- Parameterized queries via EF Core
- Uploaded files are virus-scanned (ClamAV) and re-encoded (ImageSharp)

### Error Handling

- `ExceptionHandlingMiddleware` for centralized handling
- Correlation IDs on every request for tracing
- No stack traces in production responses

### Data Protection

- ASP.NET Data Protection API for token providers
- HTTPS redirection in production, CORS restricted to configured origins
- Secrets never leave Vault except into process memory at startup

## Development Guidelines

### Naming Conventions

- Controllers: `{Entity}Controller`
- Commands: `{Action}{Entity}Command` (mutations); Queries: `{Action}{Entity}Query` (reads)
- Handlers: `{Action}{Entity}Handler`, implementing `IRequestHandler<TRequest, TResponse>`
- DTOs: `{Action}{Entity}Dto`

### Adding a New Module

1. Create the project at `src/Modules/BeeLogistics.Modules.{Name}/`
2. Add `DependencyInjection.cs` with an extension method:
   ```csharp
   public static IMvcBuilder Add{Name}Module(this IMvcBuilder builder, string connectionString)
   {
       // Register services, DbContext, validators, consumers
       return builder;
   }
   ```
3. Register it in the module chain in `Program.cs`
4. Register the module's assembly for MediatR and FluentValidation
5. **Register the new `DbContext` in `DbSeeder.cs`** — omitting this is how the Accounting and
   Offers migrations previously ended up never running

### Testing

- Unit tests for domain logic and handlers
- Integration tests for endpoints and database behaviour
- Tests live in `tests/BeeLogistics.Tests` (xUnit, coverage via Coverlet)
- Run with `dotnet test`

### Logging

- Serilog for structured logging, shipped to Seq
- Include the correlation ID in log scopes
- OpenTelemetry traces and Prometheus metrics are exported from the API

## Contributing: Issues, Branches and Merge Requests

All work starts as a GitLab issue and lands through a merge request. Nothing is pushed straight to
`dev`, `staging` or `main`.

### 1. Create the issue

Open an issue in the project and pick a template from the **"Choose a template"** dropdown:

| Template | Use it for |
|---|---|
| **Default** | Quick notes and small tasks — just *What & Why* and *Done When* |
| **Bug** | Defects. Requires affected code with `file.cs:line`, concrete impact, reproduction steps, expected vs actual, verification plan and severity |
| **Feature** | New capability. Requires the problem, proposed solution, endpoint changes, database changes, acceptance criteria and explicit out-of-scope |

The templates live in `.gitlab/issue_templates/` and are version-controlled — improve them in an MR
like any other file.

Two fields carry the most weight and are most often skipped:

- **Endpoint and database changes.** Mobile and back-office clients depend on the API surface, so
  flag changes at issue time rather than at merge time.
- **Out of scope** (Feature template). This is what keeps the resulting MR reviewable.

### 2. Create the branch and MR from the issue

Use **Create merge request** on the issue itself. That opens a draft MR, links it to the issue and
creates the branch. GitLab proposes `<issue-iid>-<slug>`; rename it to this repo's convention:

```
feat-#<issue-iid>-<short-kebab-slug>
```

For example `feat-#39-location-history-read-path-and-retention`. Keeping the issue number in the
branch name is what makes the merge history readable months later. (Older branches use
`features/<n>-<slug>` — that form is retired, don't copy it.)

Branch off **`dev`** unless you are fixing something on `staging` or `main`.

### 3. Work the branch

- Keep the MR scoped to the issue. If you find something unrelated, open another issue.
- Add a migration in the owning module for any schema change.
- Add or update tests in `tests/BeeLogistics.Tests` when behaviour changes.
- Leave the MR as **Draft:** until it is ready for review — draft MRs still run the pipeline.

### 4. Fill in the merge request

The MR template (`.gitlab/merge_request_templates/Default.md`) is applied automatically. Complete
every section, in particular:

- **Why the MR** — the problem, not a restatement of the diff
- **Endpoint changes** and **Database changes** — the two things reviewers and client developers
  actually need
- **Testing** — tick what you actually ran
- **Breaking changes** — anything affecting mobile clients or integrations
- **Deployment notes** — new CI variables, new Vault keys, migrations that need care
- **Closes #`<issue>`** — so the issue closes automatically on merge

### 5. Review and pipeline

Opening the MR triggers a merge-request pipeline: Gitleaks, Checkov, Hadolint and Trivy, then
restore → build → test → SAST → publish. See [CI/CD Pipeline](#cicd-pipeline).

- The pipeline must be green before merge. Security jobs are report-only, but read their output —
  they do not fail the pipeline when they find something.
- Address review comments with follow-up commits on the same branch; the pipeline re-runs.
- Remove the **Draft:** prefix when ready.

### 6. Merge and promote

- Merge into **`dev`** — merge commits, not squash, so the branch name stays in the history
  (`Merge branch 'feat-#39-...' into 'dev'`).
- Promotion runs **`dev` → `staging` → `main`** through merge requests as well. `main` is
  production and deploys via a manual play button.

## CI/CD Pipeline

Defined in `.gitlab-ci.yml`. Runner routing is by branch: `main` uses the `production`-tagged
runner, everything else the `dev`-tagged runner.

| Trigger | What runs |
|---|---|
| Merge request into `main`, `dev` or `staging` | security → restore → build → test → SAST → publish |
| Push to `main`, `dev` or `staging` | the full pipeline for that branch, including Docker build and deploy |

Stages: `prepare`, `security`, `restore`, `build`, `test`, `sast`, `publish`, `docker`, `deploy`.

**Security jobs** run on every branch and MR and are all report-only (`allow_failure: true`):
Gitleaks (secrets), Checkov (Dockerfile IaC), Hadolint (Dockerfile lint), Trivy (filesystem vulns,
misconfig, secrets) and SAST for vulnerable NuGet packages. Docker builds additionally get a Trivy
CVE scan and a Syft SBOM.

Deployments are **manual** — find the play button under **Build → Pipelines**.

NuGet packages are shared across jobs through a host bind mount at `/nuget` rather than GitLab's
cache; the runner must be configured with
`volumes = ["/srv/gitlab-runner/nuget:/nuget", "/var/run/docker.sock:/var/run/docker.sock"]`.

## Docker Deployment

### Compose Services

- **backend**: ASP.NET Core API
- **redis**: cache and SignalR backplane
- **rabbitmq**: MassTransit broker
- **redis-insight**: Redis browser UI

PostgreSQL is **not** part of the compose stack — the database is external and its connection
string comes from Vault. See `docker-compose.yml` for the standard stack,
`docker-compose.liveness.yml` for the liveness service, and `README.DEV.md` for the hot-reload
development stack.

### Volumes

- `redis_data`: cache persistence
- `rabbitmq_data`: broker persistence
- `uploads_data`: file uploads (driver documents, proof of delivery)

## Seeded Admin Account

`DbSeeder` creates an initial administrator on first run. The email and password come from Vault
(`SEED_DEFAULT_PASSWORD` in `bee/config`) — they are not hardcoded and must not be committed here.
Rotate the seeded password immediately in any environment that is reachable from outside.

## License

[Specify your license here]

## Support

Open an issue in this project using the appropriate template. For anything urgent or
security-sensitive, contact the development team directly rather than filing a public issue.
