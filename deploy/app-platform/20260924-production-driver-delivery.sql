-- BarTide production driver delivery extension. Run once in the Supabase SQL editor.
-- Additive only: preserves existing customer/order/payment rows and settings.
-- Does not enable Stripe or move money. Can be rerun safely.
BEGIN;
SET LOCAL search_path = tide_casa;
SET LOCAL lock_timeout = '10s';
DO $$
BEGIN
 IF current_schema() IS DISTINCT FROM 'tide_casa' OR array_length(current_schemas(false),1) <> 1
 THEN RAISE EXCEPTION 'Expected only the tide_casa application schema'; END IF;
 IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='tide_api' AND NOT rolsuper AND NOT rolbypassrls)
 THEN RAISE EXCEPTION 'Expected the existing restricted tide_api runtime role'; END IF;
 IF to_regclass('tide_casa.bartide_customers') IS NULL OR to_regclass('tide_casa.bartide_enhanced_members') IS NULL
 THEN RAISE EXCEPTION 'Existing BarTide schema is required'; END IF;
 IF NOT pg_has_role(current_user,(SELECT nspowner FROM pg_namespace WHERE nspname='tide_casa'),'USAGE')
 THEN RAISE EXCEPTION 'Run this extension as the application schema owner'; END IF;
END $$;
SELECT pg_advisory_xact_lock(hashtext(current_database()),hashtext(current_schema()));
CREATE TABLE IF NOT EXISTS tide_delivery_locations (
    tenant_id TEXT NOT NULL, driver_id TEXT NOT NULL, order_id TEXT NOT NULL,
    user_id TEXT NOT NULL, assignment_at TEXT NOT NULL, session_hash TEXT NOT NULL,
    simulated BIGINT NOT NULL, sequence BIGINT NOT NULL,
    started_at TEXT NOT NULL, received_at TEXT NOT NULL, position_json TEXT NOT NULL,
    PRIMARY KEY (tenant_id,driver_id), UNIQUE(tenant_id,order_id)
);

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

CREATE TABLE IF NOT EXISTS tide_network_drivers (
    id TEXT PRIMARY KEY, user_id TEXT NOT NULL UNIQUE, email TEXT NOT NULL,
    name TEXT NOT NULL, bio TEXT NOT NULL, zips_json TEXT NOT NULL,
    listed BIGINT NOT NULL DEFAULT 0 CHECK(listed IN (0,1)),
    capacity BIGINT NOT NULL DEFAULT 1 CHECK(capacity BETWEEN 1 AND 10),
    version BIGINT NOT NULL DEFAULT 1, created_at TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS tide_network_hires (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL REFERENCES bartide_customers(id),
    driver_id TEXT NOT NULL REFERENCES tide_network_drivers(id),
    status TEXT NOT NULL CHECK(status IN ('offered','active','declined','ended')),
    pay_per_delivery_cents BIGINT NOT NULL CHECK(pay_per_delivery_cents BETWEEN 50 AND 100000),
    notes TEXT NOT NULL, member_id TEXT UNIQUE REFERENCES bartide_enhanced_members(id),
    version BIGINT NOT NULL DEFAULT 1, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
    updated_by TEXT NOT NULL, accepted_at TEXT, ended_at TEXT,
    UNIQUE(tenant_id,driver_id)
);
CREATE INDEX IF NOT EXISTS ix_network_hires_driver ON tide_network_hires(driver_id,status);

CREATE TABLE IF NOT EXISTS tide_driver_payables (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, order_id TEXT NOT NULL UNIQUE,
    member_id TEXT NOT NULL, user_id TEXT NOT NULL, driver_name TEXT NOT NULL,
    source TEXT NOT NULL CHECK(source IN ('network','own')), hire_id TEXT,
    agreed_pay_cents BIGINT NOT NULL, completed_at TEXT NOT NULL, order_number TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS tide_driver_payables_tenant ON tide_driver_payables(tenant_id,completed_at);
CREATE INDEX IF NOT EXISTS tide_driver_payables_user ON tide_driver_payables(user_id,completed_at);
CREATE TABLE IF NOT EXISTS tide_driver_payment_states (
    payment_id TEXT NOT NULL, environment TEXT NOT NULL,
    driver_pay_cents BIGINT NOT NULL, fee_cents BIGINT NOT NULL, total_cents BIGINT NOT NULL,
    status TEXT NOT NULL, version BIGINT NOT NULL, current_attempt_id TEXT,
    approved_by TEXT NOT NULL, approved_at TEXT NOT NULL, updated_at TEXT NOT NULL,
    PRIMARY KEY(payment_id,environment)
);
CREATE TABLE IF NOT EXISTS tide_driver_payment_attempts (
    id TEXT PRIMARY KEY, payment_id TEXT NOT NULL, environment TEXT NOT NULL,
    account_id TEXT NOT NULL, user_id TEXT NOT NULL, request_json TEXT NOT NULL,
    status TEXT NOT NULL, session_id TEXT, checkout_url TEXT,
    created_at TEXT NOT NULL, updated_at TEXT NOT NULL, lease_key TEXT, lease_until TEXT,
    UNIQUE(environment,session_id)
);
CREATE TABLE IF NOT EXISTS tide_driver_payout_accounts (
    user_id TEXT NOT NULL, environment TEXT NOT NULL, account_id TEXT,
    create_key TEXT NOT NULL, create_json TEXT NOT NULL, state TEXT NOT NULL,
    created_at TEXT NOT NULL, updated_at TEXT NOT NULL, lease_key TEXT, lease_until TEXT,
    PRIMARY KEY(user_id,environment), UNIQUE(environment,account_id)
);

ALTER TABLE tide_delivery_locations ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON tide_delivery_locations FROM PUBLIC, anon, authenticated;
DROP POLICY IF EXISTS driver_runtime ON tide_delivery_locations;
CREATE POLICY driver_runtime ON tide_delivery_locations TO tide_api USING (true) WITH CHECK (true);
GRANT SELECT,INSERT,UPDATE,DELETE ON tide_delivery_locations TO tide_api;
ALTER TABLE tide_delivery_dispatch ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON tide_delivery_dispatch FROM PUBLIC, anon, authenticated;
DROP POLICY IF EXISTS driver_runtime ON tide_delivery_dispatch;
CREATE POLICY driver_runtime ON tide_delivery_dispatch TO tide_api USING (true) WITH CHECK (true);
GRANT SELECT,INSERT,UPDATE,DELETE ON tide_delivery_dispatch TO tide_api;
ALTER TABLE tide_delivery_drivers ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON tide_delivery_drivers FROM PUBLIC, anon, authenticated;
DROP POLICY IF EXISTS driver_runtime ON tide_delivery_drivers;
CREATE POLICY driver_runtime ON tide_delivery_drivers TO tide_api USING (true) WITH CHECK (true);
GRANT SELECT,INSERT,UPDATE,DELETE ON tide_delivery_drivers TO tide_api;
ALTER TABLE tide_network_drivers ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON tide_network_drivers FROM PUBLIC, anon, authenticated;
DROP POLICY IF EXISTS driver_runtime ON tide_network_drivers;
CREATE POLICY driver_runtime ON tide_network_drivers TO tide_api USING (true) WITH CHECK (true);
GRANT SELECT,INSERT,UPDATE,DELETE ON tide_network_drivers TO tide_api;
ALTER TABLE tide_network_hires ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON tide_network_hires FROM PUBLIC, anon, authenticated;
DROP POLICY IF EXISTS driver_runtime ON tide_network_hires;
CREATE POLICY driver_runtime ON tide_network_hires TO tide_api USING (true) WITH CHECK (true);
GRANT SELECT,INSERT,UPDATE,DELETE ON tide_network_hires TO tide_api;
ALTER TABLE tide_driver_payables ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON tide_driver_payables FROM PUBLIC, anon, authenticated;
DROP POLICY IF EXISTS driver_runtime ON tide_driver_payables;
CREATE POLICY driver_runtime ON tide_driver_payables TO tide_api USING (true) WITH CHECK (true);
GRANT SELECT,INSERT,UPDATE,DELETE ON tide_driver_payables TO tide_api;
ALTER TABLE tide_driver_payment_states ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON tide_driver_payment_states FROM PUBLIC, anon, authenticated;
DROP POLICY IF EXISTS driver_runtime ON tide_driver_payment_states;
CREATE POLICY driver_runtime ON tide_driver_payment_states TO tide_api USING (true) WITH CHECK (true);
GRANT SELECT,INSERT,UPDATE,DELETE ON tide_driver_payment_states TO tide_api;
ALTER TABLE tide_driver_payment_attempts ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON tide_driver_payment_attempts FROM PUBLIC, anon, authenticated;
DROP POLICY IF EXISTS driver_runtime ON tide_driver_payment_attempts;
CREATE POLICY driver_runtime ON tide_driver_payment_attempts TO tide_api USING (true) WITH CHECK (true);
GRANT SELECT,INSERT,UPDATE,DELETE ON tide_driver_payment_attempts TO tide_api;
ALTER TABLE tide_driver_payout_accounts ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON tide_driver_payout_accounts FROM PUBLIC, anon, authenticated;
DROP POLICY IF EXISTS driver_runtime ON tide_driver_payout_accounts;
CREATE POLICY driver_runtime ON tide_driver_payout_accounts TO tide_api USING (true) WITH CHECK (true);
GRANT SELECT,INSERT,UPDATE,DELETE ON tide_driver_payout_accounts TO tide_api;
COMMIT;
SELECT 'BarTide driver database update complete' AS result;
