-- New service purchases are separate from legacy $600 one-time records.
-- No existing customer is subscribed, charged, or migrated by this change.
CREATE TABLE tide_service_orders (
 id TEXT PRIMARY KEY,
 tenant_id TEXT NOT NULL REFERENCES bartide_customers(id),
 environment TEXT NOT NULL CHECK(environment IN ('sandbox','live')),
 status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','processing','paid','expired','failed')),
 request_json TEXT NOT NULL,
 initial_cents INTEGER NOT NULL CHECK(initial_cents BETWEEN 0 AND 90000),
 monthly_cents INTEGER NOT NULL CHECK(monthly_cents=5000),
 total_cents INTEGER NOT NULL CHECK(total_cents=initial_cents+monthly_cents),
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
CREATE UNIQUE INDEX tide_service_active ON tide_service_orders(tenant_id,environment) WHERE status NOT IN ('expired','failed');
CREATE TABLE tide_service_invoices (
 id TEXT PRIMARY KEY,
 order_id TEXT NOT NULL REFERENCES tide_service_orders(id),
 kind TEXT NOT NULL CHECK(kind IN ('initial','recurring')),
 amount_cents INTEGER NOT NULL CHECK(amount_cents>=5000),
 status TEXT NOT NULL,
 hosted_url TEXT,
 paid_at TEXT,
 refunded_cents INTEGER NOT NULL DEFAULT 0 CHECK(refunded_cents>=0),
 refund_pending_cents INTEGER NOT NULL DEFAULT 0 CHECK(refund_pending_cents>=0),
 refund_failed_cents INTEGER NOT NULL DEFAULT 0 CHECK(refund_failed_cents>=0),
 refund_revision INTEGER NOT NULL DEFAULT 0,
 updated_at TEXT NOT NULL
);
CREATE INDEX tide_service_invoice_order ON tide_service_invoices(order_id);
CREATE TABLE tide_service_refunds (
 id TEXT PRIMARY KEY,
 invoice_id TEXT NOT NULL REFERENCES tide_service_invoices(id),
 charge_id TEXT NOT NULL,
 amount_cents INTEGER NOT NULL CHECK(amount_cents>0),
 status TEXT NOT NULL CHECK(status IN ('pending','requires_action','succeeded','failed','canceled')),
 updated_at TEXT NOT NULL
);
CREATE TABLE tide_service_refund_sync (
 invoice_id TEXT PRIMARY KEY REFERENCES tide_service_invoices(id),
 charge_id TEXT NOT NULL,
 revision INTEGER NOT NULL DEFAULT 0,
 token TEXT NOT NULL DEFAULT ''
);
CREATE TABLE tide_service_events (
 id TEXT PRIMARY KEY,
 order_id TEXT NOT NULL REFERENCES tide_service_orders(id),
 event_type TEXT NOT NULL,
 processed_at TEXT NOT NULL
);
