CREATE TABLE bartide_payment_orders (
 id TEXT PRIMARY KEY,
 tenant_id TEXT NOT NULL REFERENCES bartide_customers(id),
 environment TEXT NOT NULL CHECK(environment IN ('sandbox','live')),
 channel TEXT NOT NULL CHECK(channel IN ('checkout','invoice')),
 status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','draft','open','processing','paid','payment_failed','failed','expired','void','partially_refunded','refunded','refund_pending')),
 payment_status TEXT NOT NULL DEFAULT 'pending' CHECK(payment_status IN ('pending','draft','open','processing','paid','payment_failed','failed','expired','void')),
 amount_cents INTEGER NOT NULL CHECK(amount_cents = 60000),
 currency TEXT NOT NULL CHECK(currency = 'usd'),
 request_json TEXT NOT NULL,
 provider_id TEXT UNIQUE,
 customer_id TEXT,
 invoice_item_id TEXT,
 hosted_url TEXT,
 paid_at TEXT,
 refunded_cents INTEGER NOT NULL DEFAULT 0,
 refund_pending_cents INTEGER NOT NULL DEFAULT 0,
 refund_failed_cents INTEGER NOT NULL DEFAULT 0,
 refund_status TEXT NOT NULL DEFAULT 'none' CHECK(refund_status IN ('none','pending','requires_action','succeeded','failed','canceled')),
 created_at TEXT NOT NULL,
 updated_at TEXT NOT NULL
);
CREATE UNIQUE INDEX idx_bartide_payment_active ON bartide_payment_orders(tenant_id,environment)
 WHERE status NOT IN ('failed','expired','void');
CREATE INDEX idx_bartide_payment_tenant ON bartide_payment_orders(tenant_id,created_at);
CREATE TABLE bartide_payment_events (
 id TEXT PRIMARY KEY,
 order_id TEXT NOT NULL REFERENCES bartide_payment_orders(id),
 event_type TEXT NOT NULL,
 processed_at TEXT NOT NULL
);
CREATE TABLE bartide_payment_refunds (
 refund_id TEXT PRIMARY KEY,
 charge_id TEXT NOT NULL,
 order_id TEXT NOT NULL REFERENCES bartide_payment_orders(id),
 amount_cents INTEGER NOT NULL CHECK(amount_cents > 0),
 status TEXT NOT NULL CHECK(status IN ('pending','requires_action','succeeded','failed','canceled')),
 updated_at TEXT NOT NULL
);
CREATE INDEX idx_bartide_refunds_order ON bartide_payment_refunds(order_id);
CREATE TABLE bartide_payment_refund_sync (
 charge_id TEXT PRIMARY KEY,
 order_id TEXT NOT NULL REFERENCES bartide_payment_orders(id),
 revision INTEGER NOT NULL DEFAULT 0,
 token TEXT NOT NULL DEFAULT ''
);
