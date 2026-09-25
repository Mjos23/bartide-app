-- New purchases require the build plus the first maintenance month at checkout.
-- Earlier paid and pending contracts retain their original prices and billing schedule.
-- FeatureMigrator suspends foreign-key enforcement outside the transaction,
-- validates every relationship before commit, and restores enforcement afterward.
CREATE TABLE "tide_service_orders_repriced" (
 id TEXT PRIMARY KEY,
 tenant_id TEXT NOT NULL REFERENCES bartide_customers(id),
 environment TEXT NOT NULL CHECK(environment IN ('sandbox','live')),
 status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','processing','paid','expired','failed')),
 request_json TEXT NOT NULL,
 initial_cents INTEGER NOT NULL CHECK(initial_cents BETWEEN 0 AND CASE WHEN monthly_cents=19900 THEN 180000 ELSE 90000 END),
 monthly_cents INTEGER NOT NULL CHECK(monthly_cents IN (5000,14900,19900)),
 total_cents INTEGER NOT NULL CHECK((monthly_cents IN (5000,19900) AND total_cents=initial_cents+monthly_cents) OR (monthly_cents=14900 AND total_cents=initial_cents)),
 app_stores INTEGER NOT NULL DEFAULT 0 CHECK(app_stores IN (0,1)),
 session_id TEXT UNIQUE,
 subscription_id TEXT UNIQUE,
 customer_id TEXT,
 initial_invoice_id TEXT UNIQUE,
 subscription_status TEXT NOT NULL DEFAULT 'pending',
 cancel_at_period_end INTEGER NOT NULL DEFAULT 0,
 period_end INTEGER,
 paid_at TEXT,
 subscription_revision INTEGER NOT NULL DEFAULT 0,
 created_at TEXT NOT NULL,
 updated_at TEXT NOT NULL
);
INSERT INTO tide_service_orders_repriced SELECT * FROM tide_service_orders;
DROP TABLE tide_service_orders;
ALTER TABLE tide_service_orders_repriced RENAME TO tide_service_orders;
CREATE UNIQUE INDEX tide_service_active ON tide_service_orders(tenant_id,environment) WHERE status NOT IN ('expired','failed');
