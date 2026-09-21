CREATE TABLE tide_business_posts (
 id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL REFERENCES bartide_customers(id),
 title TEXT NOT NULL, body TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN('draft','published','hidden')),
 version INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, published_at TEXT, updated_at TEXT NOT NULL
);
CREATE INDEX idx_business_posts_feed ON tide_business_posts(tenant_id,state,published_at);
CREATE TABLE tide_business_post_requests (
 tenant_id TEXT NOT NULL REFERENCES bartide_customers(id), request_key TEXT NOT NULL,
 actor_id TEXT NOT NULL, request_hash TEXT NOT NULL, post_id TEXT NOT NULL REFERENCES tide_business_posts(id), created_at TEXT NOT NULL,
 PRIMARY KEY(tenant_id,request_key)
);
CREATE TABLE tide_push_subscriptions (
 id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL REFERENCES bartide_customers(id), user_id TEXT NOT NULL,
 endpoint TEXT NOT NULL, endpoint_hash TEXT NOT NULL, p256dh TEXT NOT NULL, auth TEXT NOT NULL,
 active INTEGER NOT NULL CHECK(active IN(0,1)), generation INTEGER NOT NULL DEFAULT 0,
 created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
 UNIQUE(tenant_id,endpoint_hash)
);
CREATE INDEX idx_push_subscription_owner ON tide_push_subscriptions(tenant_id,user_id,active);
CREATE TABLE tide_push_outbox (
 post_id TEXT NOT NULL REFERENCES tide_business_posts(id), subscription_id TEXT NOT NULL REFERENCES tide_push_subscriptions(id),
 subscription_generation INTEGER NOT NULL,
 state TEXT NOT NULL CHECK(state IN('pending','sending','sent','cancelled','failed')),
 attempts INTEGER NOT NULL DEFAULT 0, next_attempt_at TEXT NOT NULL,
 lease_key TEXT, lease_until TEXT, last_status INTEGER, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
 PRIMARY KEY(post_id,subscription_id)
);
CREATE INDEX idx_push_outbox_dispatch ON tide_push_outbox(state,next_attempt_at,lease_until);
