CREATE TABLE tide_merchant_accounts (
 tenant_id TEXT NOT NULL REFERENCES bartide_customers(id),
 environment TEXT NOT NULL CHECK(environment='test'), account_id TEXT,
 request_key TEXT NOT NULL UNIQUE, request_json TEXT NOT NULL,
 state TEXT NOT NULL, checked_at TEXT, version INTEGER NOT NULL DEFAULT 0,
 lease_key TEXT, lease_until TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
 PRIMARY KEY(tenant_id,environment), UNIQUE(environment,account_id)
);
CREATE TABLE tide_restaurant_payment_attempts (
 id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL REFERENCES bartide_customers(id),
 order_id TEXT NOT NULL UNIQUE REFERENCES bartide_enhanced_orders(id),
 environment TEXT NOT NULL CHECK(environment='test'), account_id TEXT NOT NULL,
 request_hash TEXT NOT NULL, amount_cents INTEGER NOT NULL CHECK(amount_cents>0),
 currency TEXT NOT NULL CHECK(currency='usd'), provider_request_json TEXT NOT NULL,
 state TEXT NOT NULL, session_id TEXT, intent_id TEXT, charge_id TEXT, expires_at TEXT,
 refunded_cents INTEGER NOT NULL DEFAULT 0, pending_refund_cents INTEGER NOT NULL DEFAULT 0,
 client_hash TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 0,
 lease_key TEXT, lease_until TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
 UNIQUE(environment,account_id,session_id), UNIQUE(environment,account_id,intent_id)
);
CREATE INDEX idx_restaurant_payment_recovery ON tide_restaurant_payment_attempts(state,updated_at);
CREATE INDEX idx_restaurant_payment_client ON tide_restaurant_payment_attempts(tenant_id,client_hash,state);
CREATE TABLE tide_merchant_notification_inbox (
 context TEXT NOT NULL CHECK(context IN('snapshot','thin')),
 environment TEXT NOT NULL CHECK(environment='test'), account_id TEXT NOT NULL,
 event_id TEXT NOT NULL, event_type TEXT NOT NULL, object_id TEXT NOT NULL,
 state TEXT NOT NULL, lease_key TEXT, lease_until TEXT,
 attempts INTEGER NOT NULL DEFAULT 0, last_error TEXT,
 created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
 PRIMARY KEY(context,environment,account_id,event_id)
);
CREATE TABLE tide_merchant_refunds (
 environment TEXT NOT NULL CHECK(environment='test'), account_id TEXT NOT NULL,
 refund_id TEXT NOT NULL, attempt_id TEXT NOT NULL REFERENCES tide_restaurant_payment_attempts(id),
 charge_id TEXT NOT NULL, intent_id TEXT NOT NULL, amount_cents INTEGER NOT NULL,
 status TEXT NOT NULL, updated_at TEXT NOT NULL,
 PRIMARY KEY(environment,account_id,refund_id)
);
