# Development Guide

This guide provides detailed information for developers working on the Bee Logistics Backend.

## Table of Contents

- [Project Structure](#project-structure)
- [Development Workflow](#development-workflow)
- [Adding a New Feature](#adding-a-new-feature)
- [Database Migrations](#database-migrations)
- [Testing](#testing)
- [Code Style](#code-style)
- [Common Patterns](#common-patterns)
- [Troubleshooting](#troubleshooting)

## Project Structure

### Solution Organization

```
bee-app-backend-v2/
├── src/
│   ├── BeeLogistics.Api/          # Main API project (entry point)
│   ├── BeeLogistics.Shared/        # Shared components
│   └── Modules/                     # Business modules
│       ├── BeeLogistics.Modules.Identity/
│       ├── BeeLogistics.Modules.Company/
│       ├── BeeLogistics.Modules.Fleet/
│       ├── BeeLogistics.Modules.Sales/
│       ├── BeeLogistics.Modules.Operations/
│       ├── BeeLogistics.Modules.Payment/
│       ├── BeeLogistics.Modules.CRM/
│       ├── BeeLogistics.Modules.Chat/
│       ├── BeeLogistics.Modules.Notification/
│       ├── BeeLogistics.Modules.Map/
│       └── BeeLogistics.Modules.Drivers/
├── docker-compose.yml              # Docker services
├── Dockerfile                      # Backend container
└── README.md                       # Main documentation
```

### Module Structure

Each module follows a consistent structure:

```
ModuleName/
├── Domain/                         # Domain layer
│   ├── EntityName.cs              # Domain entities
│   └── ValueObjects/              # Value objects (if any)
├── Application/                    # Application layer
│   ├── Commands/                  # Write operations
│   ├── Queries/                   # Read operations
│   ├── DTOs/                      # Data transfer objects
│   ├── Handlers/                  # Command/Query handlers
│   ├── Interfaces/                # Application interfaces
│   └── Validators/                 # FluentValidation validators
├── Infrastructure/                 # Infrastructure layer
│   ├── ModuleNameDbContext.cs     # EF Core DbContext
│   ├── ModuleNameDbContextFactory.cs
│   ├── Repositories/              # Repository implementations
│   ├── Services/                  # External service integrations
│   └── Migrations/                # EF Core migrations
├── Presentation/                   # Presentation layer
│   ├── Controllers/               # API controllers
│   └── Hubs/                      # SignalR hubs (if any)
├── DependencyInjection.cs         # Module registration
└── ModuleName.csproj              # Project file
```

## Development Workflow

### 1. Setting Up Development Environment

1. **Install Prerequisites**:
   ```bash
   # Install .NET 10 SDK
   # Install PostgreSQL 16
   # Install Redis 7 (optional)
   # Install Docker (for containerized services)
   ```

2. **Clone and Restore**:
   ```bash
   git clone <repository-url>
   cd bee-app-backend-v2
   dotnet restore
   ```

3. **Configure Database**:
   - Create `appsettings.Development.json` in `src/BeeLogistics.Api/`
   - Set connection strings

4. **Run Migrations**:
   ```bash
   # The DbSeeder will run migrations on first startup
   # Or manually:
   dotnet ef database update --project src/Modules/BeeLogistics.Modules.Identity --startup-project src/BeeLogistics.Api
   ```

5. **Start Services**:
   ```bash
   # Option 1: Docker Compose
   docker-compose up -d postgres redis
   
   # Option 2: Local services
   # Start PostgreSQL and Redis locally
   ```

6. **Run Application**:
   ```bash
   cd src/BeeLogistics.Api
   dotnet run
   ```

### 2. Making Changes

1. **Create Feature Branch**:
   ```bash
   git checkout -b feature/your-feature-name
   ```

2. **Make Changes**:
   - Follow the module structure
   - Write tests for new features
   - Update documentation

3. **Test Locally**:
   ```bash
   dotnet test
   dotnet run
   ```

4. **Commit and Push**:
   ```bash
   git add .
   git commit -m "feat: add your feature"
   git push origin feature/your-feature-name
   ```

## Adding a New Feature

### Example: Adding a New Endpoint

Let's say we want to add a "Get Trucks by Status" endpoint.

#### Step 1: Create Query

Create `src/Modules/BeeLogistics.Modules.Fleet/Application/Queries/GetTrucksByStatusQuery.cs`:

```csharp
using BeeLogistics.Modules.Fleet.Application.DTOs;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Fleet.Application.Queries;

public record GetTrucksByStatusQuery(TruckStatus Status) : IRequest<Result<List<TruckDto>>>;
```

#### Step 2: Create Handler

Create `src/Modules/BeeLogistics.Modules.Fleet/Application/Handlers/GetTrucksByStatusQueryHandler.cs`:

```csharp
using BeeLogistics.Modules.Fleet.Application.DTOs;
using BeeLogistics.Modules.Fleet.Application.Interfaces;
using BeeLogistics.Modules.Fleet.Application.Queries;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Fleet.Application.Handlers;

public class GetTrucksByStatusQueryHandler : IRequestHandler<GetTrucksByStatusQuery, Result<List<TruckDto>>>
{
    private readonly ITruckRepository _repository;

    public GetTrucksByStatusQueryHandler(ITruckRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<List<TruckDto>>> Handle(GetTrucksByStatusQuery request, CancellationToken ct)
    {
        var trucks = await _repository.GetByStatusAsync(request.Status, ct);
        var dtos = trucks.Select(t => new TruckDto(
            t.Id,
            t.PlateNumber,
            t.Type,
            t.Capacity,
            t.Status,
            t.CompanyId,
            t.CurrentLocation,
            t.LastMaintenanceDate,
            t.DriverId
        )).ToList();

        return Result<List<TruckDto>>.Success(dtos);
    }
}
```

#### Step 3: Add Controller Endpoint

Update `src/Modules/BeeLogistics.Modules.Fleet/Presentation/Controllers/TrucksController.cs`:

```csharp
[HttpGet("status/{status}")]
public async Task<IActionResult> GetByStatus(TruckStatus status, CancellationToken ct)
    => FromResult(await _mediator.Send(new GetTrucksByStatusQuery(status), ct));
```

#### Step 4: Add Repository Method (if needed)

Update `src/Modules/BeeLogistics.Modules.Fleet/Application/Interfaces/ITruckRepository.cs`:

```csharp
Task<List<Truck>> GetByStatusAsync(TruckStatus status, CancellationToken ct);
```

Implement in repository.

#### Step 5: Add Validation (if needed)

Create `src/Modules/BeeLogistics.Modules.Fleet/Application/Validators/GetTrucksByStatusQueryValidator.cs`:

```csharp
using FluentValidation;
using BeeLogistics.Modules.Fleet.Application.Queries;

namespace BeeLogistics.Modules.Fleet.Application.Validators;

public class GetTrucksByStatusQueryValidator : AbstractValidator<GetTrucksByStatusQuery>
{
    public GetTrucksByStatusQueryValidator()
    {
        RuleFor(x => x.Status)
            .IsInEnum()
            .WithMessage("Invalid truck status");
    }
}
```

### Example: Adding a New Module

1. **Create Module Project**:
   ```bash
   dotnet new classlib -n BeeLogistics.Modules.NewModule
   cd src/Modules/BeeLogistics.Modules.NewModule
   ```

2. **Add Project Reference**:
   ```xml
   <ItemGroup>
     <ProjectReference Include="..\..\BeeLogistics.Shared\BeeLogistics.Shared.csproj" />
   </ItemGroup>
   ```

3. **Create Module Structure**:
   - Domain folder with entities
   - Application folder with commands/queries
   - Infrastructure folder with DbContext
   - Presentation folder with controllers

4. **Create DependencyInjection.cs**:
   ```csharp
   using Microsoft.EntityFrameworkCore;
   using Microsoft.Extensions.DependencyInjection;
   using BeeLogistics.Modules.NewModule.Infrastructure;

   namespace BeeLogistics.Modules.NewModule;

   public static class DependencyInjection
   {
       public static IMvcBuilder AddNewModuleModule(this IMvcBuilder builder, string connectionString)
       {
           builder.Services.AddDbContext<NewModuleDbContext>(options =>
               options.UseNpgsql(connectionString));

           // Register services
           builder.Services.AddScoped<INewModuleRepository, NewModuleRepository>();

           return builder;
       }
   }
   ```

5. **Register in Program.cs**:
   ```csharp
   .AddNewModuleModule(connectionString)
   ```

6. **Add to MediatR**:
   ```csharp
   typeof(NewModuleDbContext).Assembly
   ```

7. **Add Migration**:
   ```bash
   dotnet ef migrations add InitialNewModule --project src/Modules/BeeLogistics.Modules.NewModule --startup-project src/BeeLogistics.Api
   ```

8. **Update DbSeeder**:
   Add migration call in `DbSeeder.cs`

## Database Migrations

### Creating Migrations

For each module's DbContext:

```bash
# Identity module
dotnet ef migrations add MigrationName --project src/Modules/BeeLogistics.Modules.Identity --startup-project src/BeeLogistics.Api

# Fleet module
dotnet ef migrations add MigrationName --project src/Modules/BeeLogistics.Modules.Fleet --startup-project src/BeeLogistics.Api

# ... and so on for other modules
```

### Applying Migrations

**Option 1: Automatic (via DbSeeder)**
- Migrations run automatically on application startup

**Option 2: Manual**
```bash
dotnet ef database update --project src/Modules/BeeLogistics.Modules.Identity --startup-project src/BeeLogistics.Api
```

### Rolling Back Migrations

```bash
# Rollback last migration
dotnet ef database update PreviousMigrationName --project src/Modules/BeeLogistics.Modules.Identity --startup-project src/BeeLogistics.Api

# Remove last migration (if not applied)
dotnet ef migrations remove --project src/Modules/BeeLogistics.Modules.Identity --startup-project src/BeeLogistics.Api
```

### Migration Best Practices

1. **Name migrations descriptively**: `AddTruckCapacityField`, `CreateBookingTable`
2. **Keep migrations small**: One logical change per migration
3. **Test migrations**: Test both up and down migrations
4. **Review generated SQL**: Check the generated SQL before applying
5. **Never edit applied migrations**: Create new migrations to fix issues

## Testing

### Unit Testing

Create unit tests for:
- Domain logic
- Command/Query handlers
- Validators
- Repository methods

**Example**:
```csharp
[Fact]
public async Task Handle_ValidRequest_ReturnsSuccess()
{
    // Arrange
    var repository = new Mock<ITruckRepository>();
    var handler = new GetTrucksByStatusQueryHandler(repository.Object);
    var query = new GetTrucksByStatusQuery(TruckStatus.Available);

    // Act
    var result = await handler.Handle(query, CancellationToken.None);

    // Assert
    Assert.True(result.IsSuccess);
}
```

### Integration Testing

Test API endpoints with in-memory database:

```csharp
[Fact]
public async Task GetTrucks_ReturnsOk()
{
    // Arrange
    var client = _factory.CreateClient();
    client.DefaultRequestHeaders.Authorization = 
        new AuthenticationHeaderValue("Bearer", token);

    // Act
    var response = await client.GetAsync("/api/trucks");

    // Assert
    response.EnsureSuccessStatusCode();
}
```

### Running Tests

```bash
# Run all tests
dotnet test

# Run specific test project
dotnet test tests/BeeLogistics.Modules.Fleet.Tests

# Run with coverage
dotnet test /p:CollectCoverage=true
```

## Code Style

### Naming Conventions

- **Classes**: PascalCase (`TruckController`, `GetTrucksQuery`)
- **Methods**: PascalCase (`GetAll`, `CreateBooking`)
- **Properties**: PascalCase (`PlateNumber`, `Status`)
- **Private fields**: camelCase with underscore (`_repository`, `_mediator`)
- **Constants**: PascalCase (`UserRoles.Admin`)
- **Interfaces**: PascalCase with `I` prefix (`ITruckRepository`)

### File Organization

- One class per file
- File name matches class name
- Group related files in folders

### Code Formatting

- Use 4 spaces for indentation
- Use `var` when type is obvious
- Use expression-bodied members when appropriate
- Use `null` checks with null-conditional operators (`?.`)

### Comments

- Use XML documentation for public APIs
- Add comments for complex business logic
- Avoid obvious comments

**Example**:
```csharp
/// <summary>
/// Gets all trucks for the current user's company.
/// </summary>
/// <param name="ct">Cancellation token</param>
/// <returns>List of trucks</returns>
[HttpGet]
public async Task<IActionResult> GetAll(CancellationToken ct)
```

## Common Patterns

### Result Pattern

Use `Result<T>` for operation outcomes:

```csharp
// Success
return Result<TruckDto>.Success(truckDto);

// Failure
return Result<TruckDto>.Failure("Truck not found");
```

### CQRS Pattern

Separate commands (writes) and queries (reads):

```csharp
// Command (mutation)
public record CreateTruckCommand(CreateTruckDto Dto) : IRequest<Result<TruckDto>>;

// Query (read)
public record GetTruckByIdQuery(Guid Id) : IRequest<Result<TruckDto>>;
```

### Repository Pattern

Abstract data access:

```csharp
public interface ITruckRepository
{
    Task<Truck?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<List<Truck>> GetAllAsync(CancellationToken ct);
    Task<Truck> AddAsync(Truck truck, CancellationToken ct);
    Task UpdateAsync(Truck truck, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}
```

### Validation Pattern

Use FluentValidation with MediatR pipeline:

```csharp
public class CreateTruckCommandValidator : AbstractValidator<CreateTruckCommand>
{
    public CreateTruckCommandValidator()
    {
        RuleFor(x => x.Dto.PlateNumber)
            .NotEmpty()
            .MaximumLength(20);
    }
}
```

### Authorization Pattern

Use role-based authorization:

```csharp
[Authorize(Roles = "Admin,Owner")]
[HttpPost]
public async Task<IActionResult> Create([FromBody] CreateTruckDto dto)
```

## Troubleshooting

### Common Issues

#### 1. Migration Errors

**Problem**: Migration fails with "relation already exists"

**Solution**:
```bash
# Check migration history
dotnet ef migrations list --project src/Modules/BeeLogistics.Modules.Identity

# Remove problematic migration
dotnet ef migrations remove --project src/Modules/BeeLogistics.Modules.Identity

# Recreate migration
dotnet ef migrations add MigrationName --project src/Modules/BeeLogistics.Modules.Identity
```

#### 2. Connection String Issues

**Problem**: Cannot connect to database

**Solution**:
- Verify connection string in `appsettings.Development.json`
- Check PostgreSQL is running
- Verify credentials
- Check firewall settings

#### 3. MediatR Handler Not Found

**Problem**: Handler not being called

**Solution**:
- Verify handler implements `IRequestHandler<TRequest, TResponse>`
- Check handler is in the same assembly as the request
- Verify assembly is registered in MediatR configuration

#### 4. Validation Not Running

**Problem**: FluentValidation not executing

**Solution**:
- Verify validator implements `AbstractValidator<TRequest>`
- Check validator is registered (auto-discovered by FluentValidation)
- Verify `ValidationBehavior` is registered in MediatR pipeline

#### 5. SignalR Connection Issues

**Problem**: SignalR hub not connecting

**Solution**:
- Verify JWT token is passed in query string: `?access_token=token`
- Check CORS configuration allows SignalR
- Verify hub is mapped in `Program.cs`

### Debugging Tips

1. **Enable Detailed Logging**:
   ```json
   {
     "Serilog": {
       "MinimumLevel": {
         "Default": "Debug"
       }
     }
   }
   ```

2. **Use Breakpoints**: Set breakpoints in handlers and controllers

3. **Check Logs**: Review Serilog output for errors

4. **Database Inspection**: Use pgAdmin or psql to inspect database state

5. **API Testing**: Use Scalar UI or Postman to test endpoints

### Performance Optimization

1. **Use Async/Await**: Always use async methods for I/O operations

2. **Pagination**: Implement pagination for list endpoints

3. **Caching**: Use Redis cache for frequently accessed data

4. **Database Indexing**: Add indexes for frequently queried fields

5. **Eager Loading**: Use `Include()` for related entities when needed

6. **Projection**: Use `Select()` to project only needed fields

## Additional Resources

- [.NET Documentation](https://docs.microsoft.com/dotnet/)
- [Entity Framework Core](https://docs.microsoft.com/ef/core/)
- [MediatR](https://github.com/jbogard/MediatR)
- [FluentValidation](https://docs.fluentvalidation.net/)
- [SignalR](https://docs.microsoft.com/aspnet/core/signalr/)

## Getting Help

- Review existing code for patterns
- Check module examples
- Consult team members
- Review documentation

