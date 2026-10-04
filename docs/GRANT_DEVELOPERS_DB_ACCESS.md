# Grant the `developers` role full access to `bee_logistics_dev_db`

Give members of the `developers` group role **read + write + migrate** (DDL)
access to every schema in the dev database, **without** turning them into a
cluster superuser.

## Why ownership transfer (not just GRANT)

- `GRANT SELECT/INSERT/...` covers read/write, but **`ALTER TABLE` / `DROP TABLE`
  require *ownership*** — they are not grantable privileges. EF Core migrations
  alter/drop existing tables, so the role must **own** the objects to migrate.
- The objects are owned by `postgres` (a superuser). Do **not** run
  `GRANT postgres TO <user>` (that hands out cluster-wide superuser) and do **not**
  run `REASSIGN OWNED BY postgres TO developers` (fails with `2BP01` because
  `postgres` also owns pinned system objects).
- Instead, transfer ownership of **only the application schemas + their objects**
  to the `developers` role. Members then inherit full access automatically.

## Scope

This is a **dev-only** procedure. Every statement below operates on the
**connected database only** (`pg_namespace` / `pg_class` are per-database
catalogs), so it cannot affect other databases in the cluster.

> Confirm the exact database name first with `\l` — it has been referred to as
> both `bee_logistics_dev_db` and `bee_logistics_db_dev`.

## Prerequisites

- Run as **`postgres`** (superuser). A superuser can assign ownership to any role.
  (If running as a non-superuser, that user must be a member of `developers`.)
- The app/runtime user must be a member of `developers` and inherit it:

```sql
-- who is a member of developers, and do they inherit?
SELECT r.rolname AS member, r.rolinherit, r.rolcanlogin
FROM pg_auth_members m
JOIN pg_roles r ON r.oid = m.member
JOIN pg_roles g ON g.oid = m.roleid
WHERE g.rolname = 'developers';

-- if a needed member is missing:
-- GRANT developers TO <appuser>;
-- if a member shows rolinherit = false:
-- ALTER ROLE <appuser> INHERIT;     -- else it must `SET ROLE developers;` per session
```

## Transfer ownership to `developers`

Connect to the dev database, then run the block. It reassigns schemas, then
tables/views/matviews (their **column-linked sequences follow automatically**),
then only **standalone** sequences — skipping serial/identity sequences that are
owned by a table column (those raise `0A000` if altered directly).

```sql
\c bee_logistics_dev_db

DO $$
DECLARE r record;
BEGIN
  -- 1. schemas
  FOR r IN
    SELECT nspname FROM pg_namespace
    WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema'
  LOOP
    EXECUTE format('ALTER SCHEMA %I OWNER TO developers', r.nspname);
  END LOOP;

  -- 2. tables / partitioned tables / views / matviews
  --    (linked serial/identity sequences move with their table)
  FOR r IN
    SELECT c.relkind, n.nspname, c.relname
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
      AND c.relkind IN ('r','p','v','m')
  LOOP
    EXECUTE format('ALTER %s %I.%I OWNER TO developers',
      CASE r.relkind WHEN 'v' THEN 'VIEW'
                     WHEN 'm' THEN 'MATERIALIZED VIEW'
                     ELSE 'TABLE' END,
      r.nspname, r.relname);
  END LOOP;

  -- 3. ONLY standalone sequences (skip ones owned by a table column)
  FOR r IN
    SELECT n.nspname, c.relname
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
      AND c.relkind = 'S'
      AND NOT EXISTS (
        SELECT 1 FROM pg_depend d
        WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid
          AND d.refclassid = 'pg_class'::regclass AND d.refobjsubid > 0
          AND d.deptype IN ('a','i')   -- 'a' = serial, 'i' = identity
      )
  LOOP
    EXECUTE format('ALTER SEQUENCE %I.%I OWNER TO developers', r.nspname, r.relname);
  END LOOP;
END $$;
```

Re-run any time after new schemas are added — it is idempotent.

> Keep ownership uniform going forward: whoever **runs migrations** should act as
> `developers` (a member with `INHERIT`, or `SET ROLE developers;`) so newly
> created tables are also owned by `developers`.

## Verifications

### 1. Everything is owned by `developers`

Expect a single row: `owner = developers`.

```sql
SELECT pg_get_userbyid(c.relowner) AS owner, count(*)
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
  AND c.relkind IN ('r','p','v','m','S')
GROUP BY 1 ORDER BY 1;
```

Per-schema breakdown (every app schema should show `developers`):

```sql
SELECT n.nspname, pg_get_userbyid(c.relowner) AS owner, count(*)
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
  AND c.relkind IN ('r','p','v','m','S')
GROUP BY 1,2 ORDER BY 1;
```

Schema owners too:

```sql
SELECT nspname, nspowner::regrole AS owner
FROM pg_namespace
WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema'
ORDER BY 1;
```

### 2. A member can actually read/write/migrate

Connect **as a `developers` member** and run:

```sql
-- read
SELECT count(*) FROM identity."Users";

-- write + DDL (rolls back, leaves nothing behind)
BEGIN;
CREATE TABLE identity._access_check (id int);
INSERT INTO identity._access_check VALUES (1);
ALTER TABLE identity._access_check ADD COLUMN note text;
DROP TABLE identity._access_check;
ROLLBACK;
```

If any step says `permission denied`, that member likely has `NOINHERIT` —
fix with `ALTER ROLE <member> INHERIT;` or use `SET ROLE developers;`.

## Optional: confine `developers` to this database only

Postgres roles are cluster-global; limit reach via the `CONNECT` gate.

```sql
-- audit which databases developers / PUBLIC can connect to
SELECT datname,
       has_database_privilege('developers', datname, 'CONNECT') AS dev_can_connect,
       has_database_privilege('public',     datname, 'CONNECT') AS public_can_connect
FROM pg_database WHERE datistemplate = false ORDER BY 1;

-- ensure access to the target DB
GRANT CONNECT ON DATABASE bee_logistics_dev_db TO developers;

-- remove the default open access on OTHER databases
-- REVOKE CONNECT ON DATABASE <other_db> FROM PUBLIC;
```
