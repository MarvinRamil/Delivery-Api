# Why No Simple "Migrate-All" Command?

## The Problem

Entity Framework Core doesn't have a built-in "migrate-all" command because:

1. **Each DbContext is Independent**: Each module (Identity, Sales, Fleet, etc.) has its own `DbContext` class
2. **Separate Migration History**: Each DbContext maintains its own migration history table (e.g., `__IdentityMigrationsHistory`, `__SalesMigrationsHistory`)
3. **EF Core Needs Context**: The `dotnet ef database update` command needs to know **which** DbContext to migrate
4. **Different Migration Paths**: Migrations are stored in different assemblies/projects

## Solutions

### Option 1: Use `--context` Flag (Recommended)

You can migrate all contexts from a single location using the `--context` flag:

```bash
# From the API project root
dotnet ef database update --context IdentityAppDbContext --project src/BeeLogistics.Api
dotnet ef database update --context SalesDbContext --project src/BeeLogistics.Api
dotnet ef database update --context FleetDbContext --project src/BeeLogistics.Api
# ... etc for each context
```

**Pros:**
- Run from one location
- No need to navigate to each module folder
- Connection string comes from appsettings.json automatically

**Cons:**
- Still need to run multiple commands (one per context)
- Need to know all context names

### Option 2: Use Migration Scripts

The `migrate-all.bat` and `migrate-all.sh` scripts automate running migrations for all contexts.

**Pros:**
- Single command runs all migrations
- Automated and repeatable

**Cons:**
- Need to maintain the script
- Must list all contexts

### Option 3: Application Startup (Current Implementation)

The application automatically runs migrations on startup via `DbSeeder.SeedAsync()`:

```csharp
await context.Database.MigrateAsync();
await services.GetRequiredService<FleetDbContext>().Database.MigrateAsync();
// ... etc
```

**Pros:**
- Automatic - no manual steps needed
- Always runs when app starts

**Cons:**
- Only runs when application starts
- Can't run migrations without starting the app
- Errors might be silently caught

## Why Not a Single Command?

EF Core can't automatically discover all DbContexts because:
- DbContexts are registered at runtime via dependency injection
- Migration files are in different assemblies
- There's no central registry of all DbContexts

## Best Practice

1. **Development**: Use `migrate-all.bat` or `migrate-all.sh` scripts
2. **Production**: Let the application run migrations on startup (current implementation)
3. **CI/CD**: Run migrations explicitly before deploying the application

## Current Setup

Your application already handles migrations automatically via `DbSeeder.SeedAsync()` in `Program.cs`. This runs when the application starts and applies all migrations for all DbContexts.

If you want to run migrations manually without starting the app, use the `migrate-all.bat` or `migrate-all.sh` scripts (after updating them to read from appsettings.json).
