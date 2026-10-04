-- Migration: AddSecurityEnhancements
-- Adds UserAgent, RequestId to AuditLogs and DeviceId, DeviceFingerprint to RefreshTokens

-- Add UserAgent and RequestId to AuditLogs table
ALTER TABLE identity."AuditLogs" 
ADD COLUMN IF NOT EXISTS "UserAgent" character varying(500) NULL;

ALTER TABLE identity."AuditLogs" 
ADD COLUMN IF NOT EXISTS "RequestId" character varying(50) NULL;

-- Add index on RequestId for correlation queries
CREATE INDEX IF NOT EXISTS "IX_AuditLogs_RequestId" 
ON identity."AuditLogs" ("RequestId");

-- Add DeviceId and DeviceFingerprint to RefreshTokens table
ALTER TABLE identity."RefreshTokens" 
ADD COLUMN IF NOT EXISTS "DeviceId" character varying(256) NULL;

ALTER TABLE identity."RefreshTokens" 
ADD COLUMN IF NOT EXISTS "DeviceFingerprint" character varying(512) NULL;

-- Add index on DeviceId for device tracking
CREATE INDEX IF NOT EXISTS "IX_RefreshTokens_DeviceId" 
ON identity."RefreshTokens" ("DeviceId");

-- Record migration in history (only if not already recorded)
-- Identity module uses public."__IdentityMigrationsHistory" (see DependencyInjection.cs)
DO $EF$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '__IdentityMigrationsHistory') THEN
        IF NOT EXISTS (SELECT 1 FROM public."__IdentityMigrationsHistory" WHERE "MigrationId" = '20260220125811_AddSecurityEnhancements') THEN
            INSERT INTO public."__IdentityMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('20260220125811_AddSecurityEnhancements', '10.0.1');
        END IF;
    END IF;
END $EF$;
