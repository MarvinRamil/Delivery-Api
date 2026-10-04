# Verify Migrations

## ✅ PostGIS Migration - SUCCESS

The PostGIS migration has been successfully applied! 

**Migration Applied**: `20260116050401_AddPostGISSupport`

### What Was Created:
- ✅ PostGIS extension enabled
- ✅ `Location` geometry columns added to `DriverLocations` and `LocationHistory` tables
- ✅ GIST spatial indexes created for fast queries
- ✅ Database triggers to auto-update Location from Latitude/Longitude
- ✅ Existing data populated with geometry values

### Verify in pgAdmin4:

Run these queries to verify:

```sql
-- 1. Check PostGIS version
SELECT PostGIS_version();

-- 2. Check if Location columns exist
SELECT column_name, data_type 
FROM information_schema.columns 
WHERE table_schema = 'map' 
  AND table_name IN ('DriverLocations', 'LocationHistory')
  AND column_name = 'Location';

-- 3. Check if spatial indexes exist
SELECT indexname, indexdef 
FROM pg_indexes 
WHERE schemaname = 'map' 
  AND indexname LIKE '%GIST%';

-- 4. Check if triggers exist
SELECT trigger_name, event_manipulation, event_object_table
FROM information_schema.triggers
WHERE trigger_schema = 'map'
  AND trigger_name LIKE '%geometry%';

-- 5. Test PostGIS function
SELECT ST_MakePoint(120.9842, 14.5995) AS test_point;
```

## ⚠️ MassTransit Outbox Migration

The outbox migration file exists but wasn't applied because it was manually created (missing Designer file).

### Options:

**Option 1: Let MassTransit Create Tables Automatically (Recommended)**
- MassTransit will automatically create the outbox tables when the application starts
- No action needed - just start your application
- Tables will be created in the `sales` schema

**Option 2: Apply SQL Manually**
If you want to create the tables now, run this SQL in pgAdmin4:

```sql
-- Create OutboxState table
CREATE TABLE IF NOT EXISTS sales."OutboxState" (
    "OutboxId" uuid NOT NULL PRIMARY KEY,
    "LockId" bigint NOT NULL,
    "RowVersion" bytea,
    "Created" timestamp with time zone NOT NULL,
    "Delivered" timestamp with time zone,
    "LastSequenceNumber" bigint
);

-- Create OutboxMessage table
CREATE TABLE IF NOT EXISTS sales."OutboxMessage" (
    "SequenceNumber" bigint NOT NULL PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    "EnqueueTime" timestamp with time zone,
    "SentTime" timestamp with time zone NOT NULL,
    "Headers" text,
    "Properties" text,
    "InboxMessageId" text,
    "InboxConsumerId" text,
    "OutboxId" uuid,
    "MessageId" uuid NOT NULL,
    "ContentType" text NOT NULL,
    "MessageType" text NOT NULL,
    "Body" text NOT NULL,
    "ConversationId" uuid,
    "CorrelationId" uuid,
    "InitiatorId" uuid,
    "RequestId" uuid,
    "SourceAddress" text,
    "DestinationAddress" text,
    "ResponseAddress" text,
    "FaultAddress" text,
    "ExpirationTime" timestamp with time zone
);

-- Create InboxState table
CREATE TABLE IF NOT EXISTS sales."InboxState" (
    "Id" bigint NOT NULL PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    "MessageId" text NOT NULL,
    "ConsumerId" text NOT NULL,
    "LockId" bigint NOT NULL,
    "RowVersion" bytea,
    "Received" timestamp with time zone NOT NULL,
    "ReceiveCount" integer NOT NULL,
    "ExpirationTime" timestamp with time zone,
    "Consumed" timestamp with time zone,
    "LockedUntil" timestamp with time zone,
    "SequenceNumber" bigint
);

-- Create indexes
CREATE UNIQUE INDEX IF NOT EXISTS "IX_InboxState_MessageId_ConsumerId" 
ON sales."InboxState" ("MessageId", "ConsumerId");

CREATE INDEX IF NOT EXISTS "IX_OutboxMessage_EnqueueTime" 
ON sales."OutboxMessage" ("EnqueueTime");

CREATE INDEX IF NOT EXISTS "IX_OutboxMessage_ExpirationTime" 
ON sales."OutboxMessage" ("ExpirationTime");

CREATE UNIQUE INDEX IF NOT EXISTS "IX_OutboxMessage_InboxMessageId_InboxConsumerId_SequenceNumber" 
ON sales."OutboxMessage" ("InboxMessageId", "InboxConsumerId", "SequenceNumber") 
WHERE "InboxMessageId" IS NOT NULL AND "InboxConsumerId" IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS "IX_OutboxMessage_OutboxId_SequenceNumber" 
ON sales."OutboxMessage" ("OutboxId", "SequenceNumber") 
WHERE "OutboxId" IS NOT NULL;

CREATE INDEX IF NOT EXISTS "IX_OutboxState_Created" 
ON sales."OutboxState" ("Created");
```

## Summary

✅ **PostGIS**: Fully migrated and working!
⚠️ **Outbox**: Will be created automatically when app starts, or you can create tables manually using SQL above

## Next Steps

1. ✅ PostGIS is ready - your geospatial queries will now use PostGIS functions
2. Start your application - MassTransit will create outbox tables automatically
3. Verify outbox tables exist: `SELECT * FROM sales."OutboxState";`
