CREATE TABLE tide_events (
 id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL REFERENCES bartide_customers(id),
 title TEXT NOT NULL, details TEXT NOT NULL, location TEXT NOT NULL,
 starts_at TEXT NOT NULL, ends_at TEXT NOT NULL, time_zone TEXT NOT NULL,
 capacity INTEGER NOT NULL CHECK(capacity BETWEEN 1 AND 500),
 state TEXT NOT NULL CHECK(state IN('draft','published','cancelled')),
 version INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
 UNIQUE(id,tenant_id)
);
CREATE INDEX idx_events_tenant ON tide_events(tenant_id,state,starts_at);
CREATE TABLE tide_event_rsvps (
 event_id TEXT NOT NULL REFERENCES tide_events(id), user_id TEXT NOT NULL,
 display_name TEXT NOT NULL, email TEXT NOT NULL,
 state TEXT NOT NULL CHECK(state IN('confirmed','cancelled','checked_in')),
 version INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
 PRIMARY KEY(event_id,user_id)
);
CREATE TABLE tide_event_rsvp_requests (
 event_id TEXT NOT NULL, user_id TEXT NOT NULL, request_key TEXT NOT NULL,
 request_hash TEXT NOT NULL, created_at TEXT NOT NULL,
 PRIMARY KEY(event_id,user_id,request_key),
 FOREIGN KEY(event_id,user_id) REFERENCES tide_event_rsvps(event_id,user_id)
);
CREATE TABLE tide_event_audit (
 id TEXT PRIMARY KEY, event_id TEXT NOT NULL REFERENCES tide_events(id),
 actor_user_id TEXT NOT NULL, action TEXT NOT NULL, details_json TEXT NOT NULL, created_at TEXT NOT NULL
);
