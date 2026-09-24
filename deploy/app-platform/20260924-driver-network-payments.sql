-- Run as the application schema owner before deploying driver network/payments.
-- Apply the existing baseline and delivery-dispatch extension first.
-- Runtime grants remain an explicit deployment step; see docs/driver-dispatch.md.
BEGIN;
DO $$
BEGIN
    IF current_schema() IS NULL OR current_schema() !~ '^tide_[a-z0-9_]{1,50}$'
       OR array_length(current_schemas(false),1) <> 1 THEN
        RAISE EXCEPTION 'Select one isolated tide_ application schema before applying driver network/payments';
    END IF;
END $$;
SELECT pg_advisory_xact_lock(hashtext(current_database()),hashtext(current_schema()));
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
REVOKE ALL ON tide_network_drivers,tide_network_hires,tide_driver_payables,
    tide_driver_payment_states,tide_driver_payment_attempts,tide_driver_payout_accounts FROM PUBLIC;
COMMIT;
