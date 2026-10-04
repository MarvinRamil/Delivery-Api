# Module layering and repository pattern

All modules follow the same structure so the codebase is consistent and easy to hand off. Controllers depend on **interfaces** (Application layer), not on DbContext or concrete repositories. This keeps things loosely coupled and makes unit testing straightforward (mock the interface).

## Layering (domain → application → infrastructure → presentation)

| Layer | Responsibility | Examples |
|-------|----------------|----------|
| **Domain** | Entities, enums, domain types | `DriverApplication`, `AuditLog`, `EmailRecord` |
| **Application** | Interfaces (repositories, services), DTOs, handlers | `IDriverApplicationRepository`, `IAuditLogRepository`, `IEmailRecordRepository`, `IAuditService` |
| **Infrastructure** | DbContext, repository implementations, external services | `DriverApplicationRepository`, `AuditLogRepository`, `EmailRecordRepository` |
| **Presentation** | Controllers only; they depend on Application interfaces | Controllers inject `I*Repository`, `IAuditService`, etc. |

## Rules

1. **Controllers do not use DbContext.** They use repository (or service) interfaces from the Application layer.
2. **Repository interfaces live in Application** (e.g. `Application/Interfaces/I*Repository.cs`).
3. **Repository implementations live in Infrastructure** (e.g. `Infrastructure/Repositories/*Repository.cs`).
4. **Dependency injection** wires interfaces to implementations in each module’s `DependencyInjection.cs`.

## Modules and repositories

| Module | Repositories (interface → implementation) | Controllers using them |
|--------|------------------------------------------|-------------------------|
| **Drivers** | `IDriverApplicationRepository`, `IDriverWalletRepository` | `DriverApplicationsController`, `DriverWalletController` (via MediatR handlers that use repos) |
| **Identity** | `IAuditLogRepository` | `AuditLogsController` |
| **Notification** | `IDeviceTokenRepository`, `IEmailRecordRepository` | `DeviceTokensController` (via MediatR), `EmailStatusController` |
| **Sales** | `IBookingRepository` | `BookingsController` |

## Unit testing

- Controllers: inject mocks of `I*Repository` and `IAuditService` (or other interfaces).
- Handlers: inject mocks of repositories.
- No need to touch the real DbContext in controller or handler tests.
