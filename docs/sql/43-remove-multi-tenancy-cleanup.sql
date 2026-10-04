-- Issue #43 - Remove multi-tenancy: post-deploy manual cleanup
--
-- The 14 RemoveTenantId migrations run automatically at startup (DbSeeder calls
-- MigrateAsync per context) and drop every tenancy column. They CANNOT drop the
-- objects below, because deleting the Company module also deleted the EF context
-- that owned them - there is no migration left to carry the change.
--
-- Run this ONCE per environment, AFTER the deploy has booted cleanly and the
-- application is confirmed healthy. It is safe to re-run (all statements are
-- IF EXISTS).
--
-- Verify first (expected on dev: 88 rows, all created by an OWASP ZAP scan
-- against the formerly [AllowAnonymous] POST /api/companies):
--
--     SELECT count(*) FROM company."Companies";
--
-- Take a backup of the table if you want the rows retained for any reason:
--
--     CREATE TABLE public."Companies_archive_43" AS SELECT * FROM company."Companies";

BEGIN;

-- The Company module's only table. Nothing in the codebase references it as of #43.
DROP TABLE IF EXISTS company."Companies";

-- The now-empty schema the module owned.
DROP SCHEMA IF EXISTS company;

-- EF's per-context migration history for the deleted CompanyDbContext.
-- Leaving this behind is harmless but misleading: it advertises a context that
-- no longer exists in the solution.
DROP TABLE IF EXISTS public."__CompanyMigrationsHistory";

COMMIT;

-- Post-check: should return 0 rows.
SELECT table_schema, table_name
FROM information_schema.tables
WHERE table_schema = 'company'
   OR table_name = '__CompanyMigrationsHistory';

-- Post-check: should return only map.DriverGeofenceStates and map.Geofences if the
-- Map migration has not yet run, and nothing at all once it has.
SELECT table_schema, table_name
FROM information_schema.columns
WHERE column_name = 'TenantId';
