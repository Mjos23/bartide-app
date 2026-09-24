-- Run as the application schema owner before deploying the driver dispatch API.
-- Set search_path to the intended single tide_ application schema first.
-- This script deliberately grants no runtime role access; see docs/driver-dispatch.md.
BEGIN;
DO $$
BEGIN
    IF current_schema() IS NULL OR current_schema() !~ '^tide_[a-z0-9_]{1,50}$'
       OR array_length(current_schemas(false),1) <> 1 THEN
        RAISE EXCEPTION 'Select one isolated tide_ application schema before applying delivery dispatch';
    END IF;
END $$;
SELECT pg_advisory_xact_lock(hashtext(current_database()),hashtext(current_schema()));
CREATE TABLE IF NOT EXISTS tide_delivery_dispatch (
    tenant_id TEXT PRIMARY KEY, automatic_assignment BIGINT NOT NULL DEFAULT 0,
    version BIGINT NOT NULL DEFAULT 0, updated_at TEXT NOT NULL, updated_by TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS tide_delivery_drivers (
    tenant_id TEXT NOT NULL, driver_id TEXT NOT NULL,
    availability TEXT NOT NULL CHECK(availability IN ('offline','available','scheduled')),
    capacity BIGINT NOT NULL CHECK(capacity BETWEEN 1 AND 10), zips_json TEXT NOT NULL,
    version BIGINT NOT NULL DEFAULT 0, last_assigned_at TEXT,
    updated_at TEXT NOT NULL, updated_by TEXT NOT NULL,
    PRIMARY KEY(tenant_id,driver_id)
);
REVOKE ALL ON tide_delivery_dispatch,tide_delivery_drivers FROM PUBLIC;
COMMIT;
