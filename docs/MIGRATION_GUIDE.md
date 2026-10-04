# Database Migration Guide

## Issue: Migrations not creating schema

If migrations aren't creating tables/schemas, follow these steps:

## Step 1: Verify Database Exists

First, ensure the database `bee_logistics_db_test` exists in PostgreSQL:

```sql
-- Connect to PostgreSQL
psql -h 100.120.42.59 -U postgres

-- List databases
\l

-- Create database if it doesn't exist
CREATE DATABASE bee_logistics_db_test;

-- Connect to the database
\c bee_logistics_db_test

-- Verify connection
SELECT version();
```

## Step 2: Check Connection String

Verify your `appsettings.json` has the correct connection string:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=100.120.42.59;Database=bee_logistics_db_test;Username=postgres;Password=p@$$W0rd@123"
  }
}
```

## Step 3: Test Connection

Run the application and check logs for connection errors:
```bash
cd src/BeeLogistics.Api
dotnet run
```

Look for:
- "Starting database seeding..."
- "Database seeding completed successfully"
- Any error messages about connection failures

## Step 4: Manual Migration (If Needed)

If automatic migrations aren't working, you can manually apply migrations:

### For Identity Module:
```bash
cd src/Modules/BeeLogistics.Modules.Identity/Infrastructure
dotnet ef database update --startup-project ../../BeeLogistics.Api/BeeLogistics.Api.csproj
```

### For Sales Module:
```bash
cd src/Modules/BeeLogistics.Modules.Sales/Infrastructure
dotnet ef database update --startup-project ../../BeeLogistics.Api/BeeLogistics.Api.csproj
```

### For Fleet Module:
```bash
cd src/Modules/BeeLogistics.Modules.Fleet/Infrastructure
dotnet ef database update --startup-project ../../BeeLogistics.Api/BeeLogistics.Api.csproj
```

### For Operations Module:
```bash
cd src/Modules/BeeLogistics.Modules.Operations/Infrastructure
dotnet ef database update --startup-project ../../BeeLogistics.Api/BeeLogistics.Api.csproj
```

### For Company Module:
```bash
cd src/Modules/BeeLogistics.Modules.Company/Infrastructure
dotnet ef database update --startup-project ../../BeeLogistics.Api/BeeLogistics.Api.csproj
```

### For Payment Module:
```bash
cd src/Modules/BeeLogistics.Modules.Payment/Infrastructure
dotnet ef database update --startup-project ../../BeeLogistics.Api/BeeLogistics.Api.csproj
```

### For Chat Module:
```bash
cd src/Modules/BeeLogistics.Modules.Chat/Infrastructure
dotnet ef database update --startup-project ../../BeeLogistics.Api/BeeLogistics.Api.csproj
```

### For CRM Module:
```bash
cd src/Modules/BeeLogistics.Modules.CRM/Infrastructure
dotnet ef database update --startup-project ../../BeeLogistics.Api/BeeLogistics.Api.csproj
```

### For Drivers Module:
```bash
cd src/Modules/BeeLogistics.Modules.Drivers/Infrastructure
dotnet ef database update --startup-project ../../BeeLogistics.Api/BeeLogistics.Api.csproj
```

### For Notification Module:
```bash
cd src/Modules/BeeLogistics.Modules.Notification/Infrastructure
dotnet ef database update --startup-project ../../BeeLogistics.Api/BeeLogistics.Api.csproj
```

### For Map Module:
```bash
cd src/Modules/BeeLogistics.Modules.Map
dotnet ef database update --startup-project ../../BeeLogistics.Api/BeeLogistics.Api.csproj
```

### For Referrals Module:
```bash
cd src/Modules/BeeLogistics.Modules.Referrals/Infrastructure
dotnet ef database update --startup-project ../../BeeLogistics.Api/BeeLogistics.Api.csproj
```

## Step 5: Verify Tables Created

After migrations, verify tables exist:

```sql
\c bee_logistics_db_test

-- List all tables
\dt

-- List all schemas
\dn

-- Check specific schema (e.g., public)
\dt public.*

-- Check migration history tables
SELECT * FROM "__IdentityMigrationsHistory";
SELECT * FROM "__SalesMigrationsHistory";
SELECT * FROM "__FleetMigrationsHistory";
-- etc.
```

## Common Issues

### Issue 1: Database doesn't exist
**Solution**: Create the database first (see Step 1)

### Issue 2: Connection refused
**Solution**: 
- Verify PostgreSQL is running
- Check firewall rules allow connection from your IP
- Verify host, port, username, and password

### Issue 3: Migrations run but no tables
**Solution**: 
- Check logs for errors during `Database.MigrateAsync()`
- Verify migrations are in the correct assembly
- Check that migrations are being applied to the correct database

### Issue 4: Permission errors
**Solution**: Ensure the PostgreSQL user has CREATE, ALTER, and DROP permissions:
```sql
GRANT ALL PRIVILEGES ON DATABASE bee_logistics_db_test TO postgres;
```

## Debugging

Enable detailed EF Core logging in `appsettings.json`:
```json
{
  "Logging": {
    "LogLevel": {
      "Microsoft.EntityFrameworkCore": "Information",
      "Microsoft.EntityFrameworkCore.Database.Command": "Information"
    }
  }
}
```

This will show all SQL commands being executed during migrations.
