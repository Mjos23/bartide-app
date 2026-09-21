-- A public inquiry and its owner lead are committed in the same D1 batch.
-- This outbox preserves the original notification body even if the owner edits the lead.
CREATE TABLE IF NOT EXISTS tide_contact_inquiries (
  id TEXT PRIMARY KEY NOT NULL,
  payload_hash TEXT NOT NULL,
  ip_hash TEXT NOT NULL,
  email_hash TEXT NOT NULL,
  notification_json TEXT NOT NULL,
  notification_state TEXT NOT NULL DEFAULT 'pending' CHECK (notification_state IN ('pending','sending','accepted','failed')),
  notification_attempts INTEGER NOT NULL DEFAULT 0,
  next_attempt_at TEXT NOT NULL,
  lease_token TEXT,
  provider_id TEXT,
  error_code TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_contact_ip_created ON tide_contact_inquiries(ip_hash,created_at);
CREATE INDEX IF NOT EXISTS idx_contact_email_created ON tide_contact_inquiries(email_hash,created_at);
CREATE INDEX IF NOT EXISTS idx_contact_created ON tide_contact_inquiries(created_at);
CREATE INDEX IF NOT EXISTS idx_contact_notification ON tide_contact_inquiries(notification_state,next_attempt_at);
