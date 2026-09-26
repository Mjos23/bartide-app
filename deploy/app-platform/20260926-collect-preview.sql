-- Collect fictional-preview storage. Run as the existing application schema owner.
-- Open the hosted debtor preview once first, so its restricted database session
-- is visible. This script refuses to guess the runtime role or create credentials.
-- Additive: touches only the new tide_collect schema and its two preview tables.
BEGIN;
SET LOCAL lock_timeout = '10s';
SET LOCAL statement_timeout = '30s';
SELECT pg_advisory_xact_lock(68412271);
DO $migration$
DECLARE
  runtime_role name;
  candidates integer;
BEGIN
  IF to_regclass('tide_casa.tide_data_protection_keys') IS NULL
     OR to_regclass('tide_casa.demo_requests') IS NULL THEN
    RAISE EXCEPTION 'Expected the existing Tide Casa production schema';
  END IF;
  IF NOT pg_has_role(current_user,(SELECT nspowner FROM pg_namespace WHERE nspname='tide_casa'),'USAGE') THEN
    RAISE EXCEPTION 'Run this update as the existing application schema owner';
  END IF;
  SELECT count(DISTINCT usename), min(usename::text)::name INTO candidates, runtime_role
    FROM pg_stat_activity WHERE datname=current_database() AND application_name='TideCasa.Collect';
  IF candidates <> 1 THEN
    RAISE EXCEPTION 'Open the hosted Collect preview once, then rerun: expected exactly one observable Collect runtime role';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname=runtime_role AND rolcanlogin
      AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole)
     OR runtime_role::text IN ('anon','authenticated','service_role','postgres','supabase_admin')
     OR pg_has_role(runtime_role,(SELECT nspowner FROM pg_namespace WHERE nspname='tide_casa'),'USAGE')
     OR NOT has_table_privilege(runtime_role,'tide_casa.tide_data_protection_keys','SELECT')
     OR NOT has_table_privilege(runtime_role,'tide_casa.tide_data_protection_keys','INSERT')
     OR has_table_privilege(runtime_role,'tide_casa.demo_requests','SELECT') THEN
    RAISE EXCEPTION 'Observed Collect connection must use the existing restricted Web role';
  END IF;
  CREATE SCHEMA IF NOT EXISTS tide_collect;
  IF NOT pg_has_role(current_user,(SELECT nspowner FROM pg_namespace WHERE nspname='tide_collect'),'USAGE') THEN
    RAISE EXCEPTION 'The existing Collect schema has an unexpected owner';
  END IF;
  REVOKE ALL ON SCHEMA tide_collect FROM PUBLIC, anon, authenticated;
  EXECUTE format('GRANT USAGE ON SCHEMA tide_collect TO %I',runtime_role);
  CREATE TABLE IF NOT EXISTS tide_collect.preview_workspaces (
    id text PRIMARY KEY, revision integer NOT NULL CHECK(revision>=0),
    expires_at timestamptz NOT NULL, ciphertext text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now());
  CREATE INDEX IF NOT EXISTS collect_preview_expiry ON tide_collect.preview_workspaces(expires_at);
  CREATE TABLE IF NOT EXISTS tide_collect.preview_access_log (
    id text PRIMARY KEY, workspace_id text NOT NULL REFERENCES tide_collect.preview_workspaces(id) ON DELETE CASCADE,
    accessed_at timestamptz NOT NULL, ciphertext text NOT NULL);
  CREATE INDEX IF NOT EXISTS collect_preview_access_workspace ON tide_collect.preview_access_log(workspace_id);
  ALTER TABLE tide_collect.preview_workspaces ENABLE ROW LEVEL SECURITY;
  ALTER TABLE tide_collect.preview_access_log ENABLE ROW LEVEL SECURITY;
  REVOKE ALL ON tide_collect.preview_workspaces,tide_collect.preview_access_log FROM PUBLIC, anon, authenticated;
  DROP POLICY IF EXISTS collect_preview_runtime ON tide_collect.preview_workspaces;
  DROP POLICY IF EXISTS collect_preview_runtime ON tide_collect.preview_access_log;
  EXECUTE format('CREATE POLICY collect_preview_runtime ON tide_collect.preview_workspaces TO %I USING (true) WITH CHECK (true)',runtime_role);
  EXECUTE format('CREATE POLICY collect_preview_runtime ON tide_collect.preview_access_log TO %I USING (true) WITH CHECK (true)',runtime_role);
  EXECUTE format('GRANT SELECT,INSERT,UPDATE,DELETE ON tide_collect.preview_workspaces TO %I',runtime_role);
  EXECUTE format('GRANT SELECT,INSERT ON tide_collect.preview_access_log TO %I',runtime_role);
  -- No schema/database CREATE, credentials, restaurant-data access or public API exposure.
  IF has_schema_privilege(runtime_role,'tide_collect','CREATE')
     OR has_table_privilege('anon','tide_collect.preview_workspaces','SELECT')
     OR has_table_privilege('authenticated','tide_collect.preview_workspaces','SELECT') THEN
    RAISE EXCEPTION 'Unexpected preview permissions; transaction rolled back';
  END IF;
  RAISE NOTICE 'Collect preview tables provisioned for observed Web role %',runtime_role;
END
$migration$;
COMMIT;
