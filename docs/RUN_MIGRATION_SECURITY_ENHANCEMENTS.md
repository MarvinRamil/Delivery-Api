# Running Database Migration: AddSecurityEnhancements

## Migration Details

**Migration Name**: `20260220125811_AddSecurityEnhancements`  
**Purpose**: Add security enhancements (UserAgent, RequestId to AuditLogs; DeviceId, DeviceFingerprint to RefreshTokens)

## Option 1: Run SQL Script Directly (Recommended)

The SQL script is at:
`src/Modules/BeeLogistics.Modules.Identity/Infrastructure/Migrations/20260220125811_AddSecurityEnhancements.sql`

### Using psql:
```bash
psql -h YOUR_HOST -U YOUR_USER -d YOUR_DATABASE -f src/Modules/BeeLogistics.Modules.Identity/Infrastructure/Migrations/20260220125811_AddSecurityEnhancements.sql
```

### Using pgAdmin or DBeaver:
1. Connect to your database
2. Open the SQL script file
3. Execute it

## Option 2: Use EF Core Migration Command

If you have the correct connection string configured:

```bash
cd src/Modules/BeeLogistics.Modules.Identity/Infrastructure
dotnet ef database update --startup-project ../../BeeLogistics.Api/BeeLogistics.Api.csproj --context IdentityAppDbContext
```

**Note**: You may need to set the connection string as an environment variable:
```powershell
$env:ConnectionStrings__DefaultConnection="Host=YOUR_HOST;Database=YOUR_DB;Username=YOUR_USER;Password=YOUR_PASSWORD"
```

## Option 3: Application Startup (If Configured)

If your application is configured to run migrations on startup (via `DbSeeder` or similar), simply start the application and the migration will be applied automatically.

## What This Migration Does

### AuditLogs Table:
- ✅ Adds `UserAgent` column (varchar(500), nullable)
- ✅ Adds `RequestId` column (varchar(50), nullable)
- ✅ Creates index on `RequestId` for faster correlation queries

### RefreshTokens Table:
- ✅ Adds `DeviceId` column (varchar(256), nullable)
- ✅ Adds `DeviceFingerprint` column (varchar(512), nullable)
- ✅ Creates index on `DeviceId` for device-based queries

## Verification

After running the migration, verify it was applied:

```sql
-- Check if columns exist
SELECT column_name, data_type, character_maximum_length 
FROM information_schema.columns 
WHERE table_schema = 'identity' 
  AND table_name IN ('AuditLogs', 'RefreshTokens')
  AND column_name IN ('UserAgent', 'RequestId', 'DeviceId', 'DeviceFingerprint');

-- Check if indexes exist
SELECT indexname 
FROM pg_indexes 
WHERE schemaname = 'identity' 
  AND indexname IN ('IX_AuditLogs_RequestId', 'IX_RefreshTokens_DeviceId');

-- Check migration history
SELECT "MigrationId", "ProductVersion" 
FROM public."__IdentityMigrationsHistory" 
WHERE "MigrationId" = '20260220125811_AddSecurityEnhancements';
```

## Rollback (If Needed)

To rollback this migration:

```sql
BEGIN;

-- Remove indexes
DROP INDEX IF EXISTS identity."IX_RefreshTokens_DeviceId";
DROP INDEX IF EXISTS identity."IX_AuditLogs_RequestId";

-- Remove columns from RefreshTokens
ALTER TABLE identity."RefreshTokens" DROP COLUMN IF EXISTS "DeviceFingerprint";
ALTER TABLE identity."RefreshTokens" DROP COLUMN IF EXISTS "DeviceId";

-- Remove columns from AuditLogs
ALTER TABLE identity."AuditLogs" DROP COLUMN IF EXISTS "RequestId";
ALTER TABLE identity."AuditLogs" DROP COLUMN IF EXISTS "UserAgent";

-- Remove migration record
DELETE FROM public."__IdentityMigrationsHistory" 
WHERE "MigrationId" = '20260220125811_AddSecurityEnhancements';

COMMIT;
```
