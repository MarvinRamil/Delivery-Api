# PostGIS Installation Guide

## What is PostGIS?

PostGIS is a **PostgreSQL extension** (not a separate database). It adds spatial/geographic capabilities to your existing PostgreSQL database.

- ✅ You keep using the same PostgreSQL server
- ✅ You keep using the same database (`bee_logistics_db_test`)
- ✅ PostGIS just adds new functions and data types

## Check if PostGIS is Already Installed

### Option 1: Using pgAdmin4 (Easiest)

1. Open pgAdmin4 and connect to your PostgreSQL server (`100.120.42.59`)
2. Navigate to your database: `bee_logistics_db_test`
3. Expand the database → **Extensions**
4. Look for `postgis` in the list
   - ✅ If you see it → PostGIS is installed! Skip to "Enable PostGIS" section
   - ❌ If you don't see it → Continue to "Install PostGIS" section

### Option 2: Using SQL Query

Run this in pgAdmin4's Query Tool:

```sql
-- Check if PostGIS is available
SELECT * FROM pg_available_extensions WHERE name = 'postgis';

-- If PostGIS is installed, this will show version:
SELECT PostGIS_version();
```

**Results:**
- If `pg_available_extensions` returns a row → PostGIS is installed on the server
- If `PostGIS_version()` works → PostGIS is enabled in your database
- If you get an error → PostGIS needs to be installed

## Install PostGIS

### If You Have Server Access (SSH)

If you can SSH into the server at `100.120.42.59`:

#### For Ubuntu/Debian:
```bash
# Connect to server
ssh user@100.120.42.59

# Install PostGIS
sudo apt-get update
sudo apt-get install postgresql-<version>-postgis

# Replace <version> with your PostgreSQL version (e.g., 14, 15, 16)
# Check version: psql --version
```

#### For CentOS/RHEL:
```bash
sudo yum install postgis
```

### If You DON'T Have Server Access

You'll need to ask your server administrator to install PostGIS. Send them this:

**Request for Server Admin:**
> "Please install the PostGIS extension for PostgreSQL on server 100.120.42.59. 
> This is needed for geospatial queries in our application.
> 
> Installation command (Ubuntu/Debian):
> `sudo apt-get install postgresql-<version>-postgis`
> 
> After installation, I can enable it in the database using pgAdmin4."

## Enable PostGIS in Your Database

Once PostGIS is installed on the server, enable it in your database using pgAdmin4:

### Method 1: Using pgAdmin4 GUI

1. Open pgAdmin4
2. Connect to your server → Database `bee_logistics_db_test`
3. Right-click on **Extensions** → **Create** → **Extension**
4. In the dialog:
   - **Name**: `postgis`
   - Click **Save**

### Method 2: Using SQL (pgAdmin4 Query Tool)

Run this SQL:

```sql
-- Enable PostGIS extension
CREATE EXTENSION IF NOT EXISTS postgis;

-- Verify installation
SELECT PostGIS_version();
```

**Expected Output:**
```
PostGIS_version
---------------
3.3 USE_GEOS=1 USE_PROJ=1
```

## After Enabling PostGIS

Once PostGIS is enabled, run the migration:

```bash
cd C:\Dev\MyBeeApp\bee-app-backend-v2\src\Modules\BeeLogistics.Modules.Map
dotnet ef database update --startup-project ..\..\BeeLogistics.Api\BeeLogistics.Api.csproj --context MapDbContext
```

## Verify PostGIS is Working

Run this in pgAdmin4 Query Tool:

```sql
-- Check PostGIS version
SELECT PostGIS_version();

-- Test PostGIS function
SELECT ST_MakePoint(120.9842, 14.5995) AS test_point;

-- Should return: 0101000000...
```

## Troubleshooting

### Error: "extension postgis is not available"

**Cause**: PostGIS is not installed on the PostgreSQL server.

**Solution**: 
- Install PostGIS on the server (see "Install PostGIS" section above)
- Or ask your server administrator to install it

### Error: "permission denied to create extension"

**Cause**: Your database user doesn't have superuser privileges.

**Solution**: 
- Ask your database administrator to run: `CREATE EXTENSION postgis;`
- Or grant your user permission to create extensions

### Can't find Extensions in pgAdmin4

**Solution**: 
- Make sure you're connected to the database (not just the server)
- Expand: Server → Databases → `bee_logistics_db_test` → Extensions
- If Extensions folder doesn't exist, you might need to refresh or check permissions

## Summary

1. ✅ **Check** if PostGIS is installed (pgAdmin4 → Extensions)
2. ❌ **If not installed**: Ask server admin or install via SSH
3. ✅ **Enable** PostGIS in your database (CREATE EXTENSION)
4. ✅ **Run** the migration
5. ✅ **Verify** with `SELECT PostGIS_version();`

## Quick Check Script

Run this in pgAdmin4 to check everything:

```sql
-- 1. Check if PostGIS extension is available
SELECT 
    CASE 
        WHEN EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'postgis') 
        THEN '✅ PostGIS is available on server'
        ELSE '❌ PostGIS is NOT installed on server'
    END AS server_status;

-- 2. Check if PostGIS is enabled in this database
SELECT 
    CASE 
        WHEN EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'postgis') 
        THEN '✅ PostGIS is enabled in database'
        ELSE '❌ PostGIS is NOT enabled in database'
    END AS database_status;

-- 3. If enabled, show version
SELECT 
    CASE 
        WHEN EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'postgis') 
        THEN PostGIS_version()
        ELSE 'PostGIS not enabled'
    END AS postgis_version;
```
