CREATE TABLE tide_demo_inbox (
 request_id TEXT PRIMARY KEY REFERENCES demo_requests(id),
 version INTEGER NOT NULL DEFAULT 0, note TEXT NOT NULL DEFAULT '',
 updated_at TEXT NOT NULL, updated_by TEXT NOT NULL
);
CREATE TABLE tide_demo_notifications (
 request_id TEXT PRIMARY KEY REFERENCES demo_requests(id),
 state TEXT NOT NULL CHECK(state IN('pending','sending','sent','review')),
 attempts INTEGER NOT NULL DEFAULT 0, first_attempt_at TEXT,
 next_attempt_at TEXT NOT NULL, lease_id TEXT, lease_until TEXT,
 payload_json TEXT, provider_id TEXT, error_code TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE INDEX idx_demo_notifications_due ON tide_demo_notifications(state,next_attempt_at);
