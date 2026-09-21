CREATE TABLE tide_media_operations (
    id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL REFERENCES bartide_customers(id),
    kind TEXT NOT NULL CHECK(kind IN('photo','menu','video')),
    request_key TEXT NOT NULL,
    request_hash TEXT NOT NULL,
    media_id TEXT NOT NULL,
    object_key TEXT NOT NULL,
    state TEXT NOT NULL CHECK(state IN('pending','ready','cleanup','removed')),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE(tenant_id,kind,request_key)
);
CREATE UNIQUE INDEX tide_media_operation_object ON tide_media_operations(object_key);
CREATE INDEX tide_media_operation_pending ON tide_media_operations(state,updated_at);
