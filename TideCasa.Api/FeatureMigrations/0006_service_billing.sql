CREATE TABLE tide_service_event_inbox (
 environment TEXT NOT NULL CHECK(environment IN ('sandbox','live')),
 event_id TEXT NOT NULL,
 event_type TEXT NOT NULL,
 object_type TEXT NOT NULL,
 object_id TEXT NOT NULL,
 state TEXT NOT NULL DEFAULT 'pending' CHECK(state IN ('pending','processing','complete','ignored')),
 lease_token TEXT,
 lease_until INTEGER NOT NULL DEFAULT 0,
 attempts INTEGER NOT NULL DEFAULT 0,
 failure_code TEXT,
 created_at TEXT NOT NULL,
 updated_at TEXT NOT NULL,
 PRIMARY KEY(environment,event_id)
);
CREATE INDEX tide_service_inbox_pending ON tide_service_event_inbox(state,lease_until,updated_at);
