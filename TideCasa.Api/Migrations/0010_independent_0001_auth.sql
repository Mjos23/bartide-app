-- Apply once AFTER the existing drizzle migrations and/or verified production import.
CREATE TABLE bartide_auth_identities (
  provider_user_id TEXT PRIMARY KEY,
  app_user_id TEXT NOT NULL UNIQUE,
  verified_email TEXT NOT NULL,
  created_at TEXT NOT NULL
);
CREATE TABLE bartide_auth_legacy_claims (
  email TEXT NOT NULL,
  legacy_user_id TEXT NOT NULL,
  approved INTEGER NOT NULL DEFAULT 0 CHECK(approved IN (0,1)),
  PRIMARY KEY(email, legacy_user_id)
);
CREATE INDEX idx_auth_legacy_user ON bartide_auth_legacy_claims(legacy_user_id);
CREATE TABLE bartide_auth_limits (id TEXT PRIMARY KEY, count INTEGER NOT NULL, expires_at INTEGER NOT NULL);

CREATE TABLE bartide_auth_sessions (
  token_hash TEXT PRIMARY KEY,
  provider_user_id TEXT NOT NULL REFERENCES bartide_auth_identities(provider_user_id),
  expires_at INTEGER NOT NULL
);
CREATE INDEX idx_auth_sessions_expiry ON bartide_auth_sessions(expires_at);
CREATE INDEX idx_auth_sessions_user ON bartide_auth_sessions(provider_user_id);
