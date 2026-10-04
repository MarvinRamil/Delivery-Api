START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM public."__IdentityMigrationsHistory" WHERE "MigrationId" = '20260211093104_AddLivenessVerifiedAt') THEN
    ALTER TABLE identity."Users" ADD "LivenessVerifiedAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM public."__IdentityMigrationsHistory" WHERE "MigrationId" = '20260211093104_AddLivenessVerifiedAt') THEN
    INSERT INTO public."__IdentityMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260211093104_AddLivenessVerifiedAt', '10.0.1');
    END IF;
END $EF$;
COMMIT;

