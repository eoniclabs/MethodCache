-- MethodCache.PostgreSql, schema version 1.
--
-- MethodCache creates these tables itself when it starts (CreateTablesIfMissing, on by default). Run this script
-- instead when the application's login may not create tables: run it once, as a role that may, then set
-- CreateTablesIfMissing = false. It is written for the default names (schema public, tables methodcache and
-- methodcache_invalidations); replace them throughout if you configured others.
--
-- The application's login then needs SELECT, INSERT, UPDATE and DELETE on the four tables:
--   GRANT SELECT, INSERT, UPDATE, DELETE ON "public"."methodcache", "public"."methodcache_schema",
--       "public"."methodcache_invalidations", "public"."methodcache_invalidations_cursor" TO your_app_login;
--
-- Requires PostgreSQL 13 or later.

BEGIN;

CREATE TABLE "public"."methodcache_schema" (
    id smallint NOT NULL PRIMARY KEY CHECK (id = 1),
    version integer NOT NULL);
INSERT INTO "public"."methodcache_schema" (id, version) VALUES (1, 1);

CREATE TABLE IF NOT EXISTS "public"."methodcache" (
    id bytea NOT NULL PRIMARY KEY,
    value bytea NOT NULL,
    expires_at timestamptz NOT NULL);
CREATE INDEX IF NOT EXISTS "methodcache_expires" ON "public"."methodcache" (expires_at);

CREATE TABLE IF NOT EXISTS "public"."methodcache_invalidations" (
    scope bytea NOT NULL,
    id bigint NOT NULL,
    instance_id text NOT NULL,
    message_type smallint NOT NULL,
    target text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (scope, id));
CREATE INDEX IF NOT EXISTS "methodcache_invalidations_created" ON "public"."methodcache_invalidations" (scope, created_at);

-- Unlogged: recovery from a crash empties it, which tells every instance that the last invalidations may be lost.
CREATE UNLOGGED TABLE IF NOT EXISTS "public"."methodcache_invalidations_cursor" (
    scope bytea NOT NULL PRIMARY KEY,
    last_id bigint NOT NULL,
    retention interval NOT NULL DEFAULT '0',
    generation uuid NOT NULL DEFAULT gen_random_uuid());

-- Values up to about 8 KB stay in the row, so a read needs no second lookup in the TOAST table. The 32-byte id is
-- PLAIN: otherwise PostgreSQL would move it out of the row first.
ALTER TABLE "public"."methodcache" ALTER COLUMN id SET STORAGE PLAIN, ALTER COLUMN value SET STORAGE MAIN;

-- lz4 compresses large values faster than the default pglz, where the server was built with it (PostgreSQL 14+).
-- EXECUTE keeps PostgreSQL 13, which has no SET COMPRESSION, from rejecting the block before it runs.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_settings WHERE name = 'default_toast_compression' AND 'lz4' = ANY(enumvals)) THEN
        EXECUTE 'ALTER TABLE "public"."methodcache" ALTER COLUMN value SET COMPRESSION lz4';
    END IF;
END $$;

COMMIT;
