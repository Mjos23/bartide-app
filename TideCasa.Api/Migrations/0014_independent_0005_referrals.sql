-- Referral access is tied to the verified account ID, never to a client-supplied profile ID.
CREATE TABLE IF NOT EXISTS tide_referral_profiles (
  id TEXT PRIMARY KEY NOT NULL,
  user_id TEXT NOT NULL UNIQUE,
  email TEXT NOT NULL COLLATE NOCASE UNIQUE,
  name TEXT NOT NULL,
  introduction TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','active','paused','declined')),
  code TEXT COLLATE NOCASE UNIQUE,
  discount_percent INTEGER NOT NULL DEFAULT 0 CHECK(discount_percent BETWEEN 0 AND 100),
  terms_version TEXT NOT NULL,
  terms_accepted_at TEXT NOT NULL,
  review_token TEXT NOT NULL DEFAULT '',
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_referral_profiles_status ON tide_referral_profiles(status,created_at);

-- event_id identifies a sale (for example, a Stripe invoice), not its webhook delivery.
-- A repeated refund update adjusts the original sale instead of adding a new commission.
CREATE TABLE IF NOT EXISTS tide_referral_sales (
  event_id TEXT PRIMARY KEY NOT NULL,
  order_id TEXT NOT NULL,
  profile_id TEXT NOT NULL REFERENCES tide_referral_profiles(id),
  kind TEXT NOT NULL CHECK(kind IN ('initial','recurring')),
  environment TEXT NOT NULL CHECK(environment IN ('sandbox','live')),
  gross_cents INTEGER NOT NULL CHECK(gross_cents > 0),
  refunded_cents INTEGER NOT NULL DEFAULT 0 CHECK(refunded_cents >= 0 AND refunded_cents <= gross_cents),
  commission_cents INTEGER NOT NULL CHECK(commission_cents >= 0),
  source_revision INTEGER NOT NULL DEFAULT 0 CHECK(source_revision >= 0),
  paid_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_referral_sales_profile ON tide_referral_sales(profile_id,environment,paid_at);

CREATE TABLE IF NOT EXISTS tide_referral_reviews (
  id TEXT PRIMARY KEY NOT NULL,
  profile_id TEXT NOT NULL REFERENCES tide_referral_profiles(id),
  reviewer_id TEXT NOT NULL,
  status TEXT NOT NULL,
  code TEXT,
  discount_percent INTEGER NOT NULL,
  created_at TEXT NOT NULL
);
