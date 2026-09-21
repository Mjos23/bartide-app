-- PostgreSQL baseline 1. Schema only; no customer records or legacy history are fabricated.
-- Execute only through PostgresSchemaMigrator in the isolated application search_path.
-- TEXT retains original timestamps/JSON/IDs; BIGINT retains SQLite signed 64-bit integers.
-- C collation preserves binary comparisons. Referral NOCASE uniqueness folds ASCII only.
-- No SQLite AUTOINCREMENT is present. Legacy migration versions are explicit, not identities.

CREATE TABLE "bartide_auth_identities" (
    "provider_user_id" TEXT COLLATE "C" NOT NULL,
    "app_user_id" TEXT COLLATE "C" NOT NULL,
    "verified_email" TEXT COLLATE "C" NOT NULL,
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_bartide_auth_identities" PRIMARY KEY ("provider_user_id"),
    CONSTRAINT "pguq_bartide_auth_identities_01" UNIQUE ("app_user_id")
);

CREATE TABLE "bartide_auth_legacy_claims" (
    "email" TEXT COLLATE "C" NOT NULL,
    "legacy_user_id" TEXT COLLATE "C" NOT NULL,
    "approved" BIGINT NOT NULL DEFAULT 0,
    CONSTRAINT "pgpk_bartide_auth_legacy_claims" PRIMARY KEY ("email", "legacy_user_id"),
    CONSTRAINT "pgck_bartide_auth_legacy_claims_01" CHECK (approved IN (0,1))
);

CREATE TABLE "bartide_auth_limits" (
    "id" TEXT COLLATE "C" NOT NULL,
    "count" BIGINT NOT NULL,
    "expires_at" BIGINT NOT NULL,
    CONSTRAINT "pgpk_bartide_auth_limits" PRIMARY KEY ("id")
);

CREATE TABLE "bartide_auth_sessions" (
    "token_hash" TEXT COLLATE "C" NOT NULL,
    "provider_user_id" TEXT COLLATE "C" NOT NULL,
    "expires_at" BIGINT NOT NULL,
    CONSTRAINT "pgpk_bartide_auth_sessions" PRIMARY KEY ("token_hash")
);

CREATE TABLE "bartide_campaigns" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "name" TEXT COLLATE "C" NOT NULL,
    "channel" TEXT COLLATE "C" NOT NULL,
    "caption" TEXT COLLATE "C" NOT NULL,
    "active" BIGINT NOT NULL DEFAULT 1,
    "clicks" BIGINT NOT NULL DEFAULT 0,
    "version" BIGINT NOT NULL DEFAULT 0,
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_bartide_campaigns" PRIMARY KEY ("id")
);

CREATE TABLE "bartide_connections" (
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "social_json" TEXT COLLATE "C" NOT NULL DEFAULT '{}',
    "version" BIGINT NOT NULL DEFAULT 0,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_bartide_connections" PRIMARY KEY ("tenant_id")
);

CREATE TABLE "bartide_contact_details" (
    "member_id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "phone" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "sms_status" TEXT COLLATE "C" NOT NULL DEFAULT 'off',
    "code_hash" TEXT COLLATE "C",
    "code_expires" TEXT COLLATE "C",
    "consent_at" TEXT COLLATE "C",
    "consent_text" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "tags_json" TEXT COLLATE "C" NOT NULL DEFAULT '[]',
    "city" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "notes" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "version" BIGINT NOT NULL DEFAULT 0,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_bartide_contact_details" PRIMARY KEY ("member_id")
);

CREATE TABLE "bartide_customers" (
    "id" TEXT COLLATE "C" NOT NULL,
    "slug" TEXT COLLATE "C" NOT NULL,
    "email" TEXT COLLATE "C" NOT NULL,
    "user_id" TEXT COLLATE "C",
    "name" TEXT COLLATE "C" NOT NULL,
    "menu_json" TEXT COLLATE "C" NOT NULL,
    "version" BIGINT NOT NULL DEFAULT 0,
    "status" TEXT COLLATE "C" NOT NULL DEFAULT 'active',
    "enrollment_note" TEXT COLLATE "C" NOT NULL,
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    "requested_plan" TEXT COLLATE "C" NOT NULL DEFAULT 'app',
    "contact_name" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "contact_phone" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "setup_notes" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "submitted_at" TEXT COLLATE "C",
    "vertical" TEXT COLLATE "C" NOT NULL DEFAULT 'bartide',
    "enrolled_at" TEXT COLLATE "C",
    "build_ready_at" TEXT COLLATE "C",
    CONSTRAINT "pgpk_bartide_customers" PRIMARY KEY ("id")
);

CREATE TABLE "bartide_enhanced_configs" (
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "settings_json" TEXT COLLATE "C" NOT NULL,
    "version" BIGINT NOT NULL DEFAULT 0,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_bartide_enhanced_configs" PRIMARY KEY ("tenant_id")
);

CREATE TABLE "bartide_enhanced_limits" (
    "id" TEXT COLLATE "C" NOT NULL,
    "count" BIGINT NOT NULL,
    "expires_at" BIGINT NOT NULL,
    CONSTRAINT "pgpk_bartide_enhanced_limits" PRIMARY KEY ("id")
);

CREATE TABLE "bartide_enhanced_members" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "name" TEXT COLLATE "C" NOT NULL,
    "email" TEXT COLLATE "C" NOT NULL,
    "user_id" TEXT COLLATE "C",
    "role" TEXT COLLATE "C" NOT NULL,
    "active" BIGINT NOT NULL DEFAULT 1,
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_bartide_enhanced_members" PRIMARY KEY ("id")
);

CREATE TABLE "bartide_enhanced_messages" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "author_id" TEXT COLLATE "C" NOT NULL,
    "author" TEXT COLLATE "C" NOT NULL,
    "body" TEXT COLLATE "C" NOT NULL,
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_bartide_enhanced_messages" PRIMARY KEY ("id")
);

CREATE TABLE "bartide_enhanced_orders" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "request_key" TEXT COLLATE "C" NOT NULL,
    "request_hash" TEXT COLLATE "C" NOT NULL,
    "tracking_hash" TEXT COLLATE "C" NOT NULL,
    "payload_json" TEXT COLLATE "C" NOT NULL,
    "status" TEXT COLLATE "C" NOT NULL,
    "fulfillment" TEXT COLLATE "C" NOT NULL,
    "driver_id" TEXT COLLATE "C",
    "version" BIGINT NOT NULL DEFAULT 0,
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_bartide_enhanced_orders" PRIMARY KEY ("id")
);

CREATE TABLE "bartide_enhanced_shifts" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "member_id" TEXT COLLATE "C" NOT NULL,
    "starts_at" TEXT COLLATE "C" NOT NULL,
    "ends_at" TEXT COLLATE "C" NOT NULL,
    "label" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_bartide_enhanced_shifts" PRIMARY KEY ("id")
);

CREATE TABLE "bartide_growth_events" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "member_id" TEXT COLLATE "C",
    "kind" TEXT COLLATE "C" NOT NULL,
    "actor" TEXT COLLATE "C" NOT NULL,
    "summary" TEXT COLLATE "C" NOT NULL,
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_bartide_growth_events" PRIMARY KEY ("id")
);

CREATE TABLE "bartide_member_passes" (
    "member_id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "token_hash" TEXT COLLATE "C" NOT NULL,
    "state" TEXT COLLATE "C" NOT NULL,
    "version" BIGINT NOT NULL DEFAULT 0,
    "issued_at" TEXT COLLATE "C" NOT NULL,
    "expires_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_bartide_member_passes" PRIMARY KEY ("member_id")
);

CREATE TABLE "bartide_menu_files" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "object_key" TEXT COLLATE "C" NOT NULL,
    "name" TEXT COLLATE "C" NOT NULL,
    "content_type" TEXT COLLATE "C" NOT NULL,
    "byte_size" BIGINT NOT NULL,
    "status" TEXT COLLATE "C" NOT NULL DEFAULT 'pending',
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_bartide_menu_files" PRIMARY KEY ("id")
);

CREATE TABLE "bartide_payment_events" (
    "id" TEXT COLLATE "C" NOT NULL,
    "order_id" TEXT COLLATE "C" NOT NULL,
    "event_type" TEXT COLLATE "C" NOT NULL,
    "processed_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_bartide_payment_events" PRIMARY KEY ("id")
);

CREATE TABLE "bartide_payment_orders" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "environment" TEXT COLLATE "C" NOT NULL,
    "channel" TEXT COLLATE "C" NOT NULL,
    "status" TEXT COLLATE "C" NOT NULL DEFAULT 'pending',
    "payment_status" TEXT COLLATE "C" NOT NULL DEFAULT 'pending',
    "amount_cents" BIGINT NOT NULL,
    "currency" TEXT COLLATE "C" NOT NULL,
    "request_json" TEXT COLLATE "C" NOT NULL,
    "provider_id" TEXT COLLATE "C",
    "customer_id" TEXT COLLATE "C",
    "invoice_item_id" TEXT COLLATE "C",
    "hosted_url" TEXT COLLATE "C",
    "paid_at" TEXT COLLATE "C",
    "refunded_cents" BIGINT NOT NULL DEFAULT 0,
    "refund_pending_cents" BIGINT NOT NULL DEFAULT 0,
    "refund_failed_cents" BIGINT NOT NULL DEFAULT 0,
    "refund_status" TEXT COLLATE "C" NOT NULL DEFAULT 'none',
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_bartide_payment_orders" PRIMARY KEY ("id"),
    CONSTRAINT "pgck_bartide_payment_orders_01" CHECK (environment IN ('sandbox','live')),
    CONSTRAINT "pgck_bartide_payment_orders_02" CHECK (channel IN ('checkout','invoice')),
    CONSTRAINT "pgck_bartide_payment_orders_03" CHECK (status IN ('pending','draft','open','processing','paid','payment_failed','failed','expired','void','partially_refunded','refunded','refund_pending')),
    CONSTRAINT "pgck_bartide_payment_orders_04" CHECK (payment_status IN ('pending','draft','open','processing','paid','payment_failed','failed','expired','void')),
    CONSTRAINT "pgck_bartide_payment_orders_05" CHECK (amount_cents = 60000),
    CONSTRAINT "pgck_bartide_payment_orders_06" CHECK (currency = 'usd'),
    CONSTRAINT "pgck_bartide_payment_orders_07" CHECK (refund_status IN ('none','pending','requires_action','succeeded','failed','canceled')),
    CONSTRAINT "pguq_bartide_payment_orders_01" UNIQUE ("provider_id")
);

CREATE TABLE "bartide_payment_refund_sync" (
    "charge_id" TEXT COLLATE "C" NOT NULL,
    "order_id" TEXT COLLATE "C" NOT NULL,
    "revision" BIGINT NOT NULL DEFAULT 0,
    "token" TEXT COLLATE "C" NOT NULL DEFAULT '',
    CONSTRAINT "pgpk_bartide_payment_refund_sync" PRIMARY KEY ("charge_id")
);

CREATE TABLE "bartide_payment_refunds" (
    "refund_id" TEXT COLLATE "C" NOT NULL,
    "charge_id" TEXT COLLATE "C" NOT NULL,
    "order_id" TEXT COLLATE "C" NOT NULL,
    "amount_cents" BIGINT NOT NULL,
    "status" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_bartide_payment_refunds" PRIMARY KEY ("refund_id"),
    CONSTRAINT "pgck_bartide_payment_refunds_01" CHECK (amount_cents > 0),
    CONSTRAINT "pgck_bartide_payment_refunds_02" CHECK (status IN ('pending','requires_action','succeeded','failed','canceled'))
);

CREATE TABLE "bartide_photos" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "object_key" TEXT COLLATE "C" NOT NULL,
    "byte_size" BIGINT NOT NULL,
    "width" BIGINT NOT NULL,
    "height" BIGINT NOT NULL,
    "status" TEXT COLLATE "C" NOT NULL DEFAULT 'pending',
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_bartide_photos" PRIMARY KEY ("id")
);

CREATE TABLE "bartide_sms_messages" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "member_id" TEXT COLLATE "C" NOT NULL,
    "phone" TEXT COLLATE "C" NOT NULL,
    "body" TEXT COLLATE "C" NOT NULL,
    "direction" TEXT COLLATE "C" NOT NULL DEFAULT 'outbound',
    "state" TEXT COLLATE "C" NOT NULL DEFAULT 'draft',
    "provider_sid" TEXT COLLATE "C",
    "error" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_bartide_sms_messages" PRIMARY KEY ("id")
);

CREATE TABLE "demo_requests" (
    "id" TEXT COLLATE "C" NOT NULL,
    "payload_hash" TEXT COLLATE "C" NOT NULL,
    "email_hash" TEXT COLLATE "C" NOT NULL,
    "request_json" TEXT COLLATE "C" NOT NULL,
    "status" TEXT COLLATE "C" NOT NULL DEFAULT 'requested',
    "notification_state" TEXT COLLATE "C" NOT NULL DEFAULT 'pending',
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_demo_requests" PRIMARY KEY ("id")
);

CREATE TABLE "fit_courses" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "title" TEXT COLLATE "C" NOT NULL,
    "description" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "published" BIGINT NOT NULL DEFAULT 0,
    "version" BIGINT NOT NULL DEFAULT 0,
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_fit_courses" PRIMARY KEY ("id")
);

CREATE TABLE "fit_learners" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "name" TEXT COLLATE "C" NOT NULL,
    "email" TEXT COLLATE "C" NOT NULL,
    "user_id" TEXT COLLATE "C",
    "active" BIGINT NOT NULL DEFAULT 1,
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_fit_learners" PRIMARY KEY ("id")
);

CREATE TABLE "fit_lessons" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "course_id" TEXT COLLATE "C" NOT NULL,
    "title" TEXT COLLATE "C" NOT NULL,
    "description" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "video_kind" TEXT COLLATE "C" NOT NULL,
    "video_source" TEXT COLLATE "C" NOT NULL,
    "position" BIGINT NOT NULL DEFAULT 1,
    "version" BIGINT NOT NULL DEFAULT 0,
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_fit_lessons" PRIMARY KEY ("id")
);

CREATE TABLE "fit_progress" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "learner_id" TEXT COLLATE "C" NOT NULL,
    "lesson_id" TEXT COLLATE "C" NOT NULL,
    "completed" BIGINT NOT NULL DEFAULT 0,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_fit_progress" PRIMARY KEY ("id")
);

CREATE TABLE "fit_videos" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "object_key" TEXT COLLATE "C" NOT NULL,
    "name" TEXT COLLATE "C" NOT NULL,
    "byte_size" BIGINT NOT NULL,
    "status" TEXT COLLATE "C" NOT NULL DEFAULT 'pending',
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_fit_videos" PRIMARY KEY ("id")
);

CREATE TABLE "tide_business_post_requests" (
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "request_key" TEXT COLLATE "C" NOT NULL,
    "actor_id" TEXT COLLATE "C" NOT NULL,
    "request_hash" TEXT COLLATE "C" NOT NULL,
    "post_id" TEXT COLLATE "C" NOT NULL,
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_business_post_requests" PRIMARY KEY ("tenant_id", "request_key")
);

CREATE TABLE "tide_business_posts" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "title" TEXT COLLATE "C" NOT NULL,
    "body" TEXT COLLATE "C" NOT NULL,
    "state" TEXT COLLATE "C" NOT NULL,
    "version" BIGINT NOT NULL DEFAULT 0,
    "created_at" TEXT COLLATE "C" NOT NULL,
    "published_at" TEXT COLLATE "C",
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_business_posts" PRIMARY KEY ("id"),
    CONSTRAINT "pgck_tide_business_posts_01" CHECK (state IN('draft','published','hidden'))
);

CREATE TABLE "tide_contact_inquiries" (
    "id" TEXT COLLATE "C" NOT NULL,
    "payload_hash" TEXT COLLATE "C" NOT NULL,
    "ip_hash" TEXT COLLATE "C" NOT NULL,
    "email_hash" TEXT COLLATE "C" NOT NULL,
    "notification_json" TEXT COLLATE "C" NOT NULL,
    "notification_state" TEXT COLLATE "C" NOT NULL DEFAULT 'pending',
    "notification_attempts" BIGINT NOT NULL DEFAULT 0,
    "next_attempt_at" TEXT COLLATE "C" NOT NULL,
    "lease_token" TEXT COLLATE "C",
    "provider_id" TEXT COLLATE "C",
    "error_code" TEXT COLLATE "C",
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_contact_inquiries" PRIMARY KEY ("id"),
    CONSTRAINT "pgck_tide_contact_inquiries_01" CHECK (notification_state IN ('pending','sending','accepted','failed'))
);

CREATE TABLE "tide_data_protection_keys" (
    "id" TEXT COLLATE "C" NOT NULL,
    "friendly_name" TEXT COLLATE "C" NOT NULL,
    "ciphertext" TEXT COLLATE "C" NOT NULL,
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_data_protection_keys" PRIMARY KEY ("id")
);

CREATE TABLE "tide_demo_inbox" (
    "request_id" TEXT COLLATE "C" NOT NULL,
    "version" BIGINT NOT NULL DEFAULT 0,
    "note" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "updated_at" TEXT COLLATE "C" NOT NULL,
    "updated_by" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_demo_inbox" PRIMARY KEY ("request_id")
);

CREATE TABLE "tide_demo_notifications" (
    "request_id" TEXT COLLATE "C" NOT NULL,
    "state" TEXT COLLATE "C" NOT NULL,
    "attempts" BIGINT NOT NULL DEFAULT 0,
    "first_attempt_at" TEXT COLLATE "C",
    "next_attempt_at" TEXT COLLATE "C" NOT NULL,
    "lease_id" TEXT COLLATE "C",
    "lease_until" TEXT COLLATE "C",
    "payload_json" TEXT COLLATE "C",
    "provider_id" TEXT COLLATE "C",
    "error_code" TEXT COLLATE "C",
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_demo_notifications" PRIMARY KEY ("request_id"),
    CONSTRAINT "pgck_tide_demo_notifications_01" CHECK (state IN('pending','sending','sent','review'))
);

CREATE TABLE "tide_document_reviews" (
    "file_id" TEXT COLLATE "C" NOT NULL,
    "state" TEXT COLLATE "C" NOT NULL,
    "notes" TEXT COLLATE "C" NOT NULL,
    "version" BIGINT NOT NULL DEFAULT 0,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_document_reviews" PRIMARY KEY ("file_id")
);

CREATE TABLE "tide_employee_reward_ledger" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "member_id" TEXT COLLATE "C" NOT NULL,
    "source_reference" TEXT COLLATE "C" NOT NULL,
    "delta" BIGINT NOT NULL,
    "reason" TEXT COLLATE "C" NOT NULL,
    "actor" TEXT COLLATE "C" NOT NULL,
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_employee_reward_ledger" PRIMARY KEY ("id"),
    CONSTRAINT "pgck_tide_employee_reward_ledger_01" CHECK (delta<>0),
    CONSTRAINT "pguq_tide_employee_reward_ledger_01" UNIQUE ("tenant_id", "source_reference")
);

CREATE TABLE "tide_event_audit" (
    "id" TEXT COLLATE "C" NOT NULL,
    "event_id" TEXT COLLATE "C" NOT NULL,
    "actor_user_id" TEXT COLLATE "C" NOT NULL,
    "action" TEXT COLLATE "C" NOT NULL,
    "details_json" TEXT COLLATE "C" NOT NULL,
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_event_audit" PRIMARY KEY ("id")
);

CREATE TABLE "tide_event_rsvp_requests" (
    "event_id" TEXT COLLATE "C" NOT NULL,
    "user_id" TEXT COLLATE "C" NOT NULL,
    "request_key" TEXT COLLATE "C" NOT NULL,
    "request_hash" TEXT COLLATE "C" NOT NULL,
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_event_rsvp_requests" PRIMARY KEY ("event_id", "user_id", "request_key")
);

CREATE TABLE "tide_event_rsvps" (
    "event_id" TEXT COLLATE "C" NOT NULL,
    "user_id" TEXT COLLATE "C" NOT NULL,
    "display_name" TEXT COLLATE "C" NOT NULL,
    "email" TEXT COLLATE "C" NOT NULL,
    "state" TEXT COLLATE "C" NOT NULL,
    "version" BIGINT NOT NULL DEFAULT 0,
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_event_rsvps" PRIMARY KEY ("event_id", "user_id"),
    CONSTRAINT "pgck_tide_event_rsvps_01" CHECK (state IN('confirmed','cancelled','checked_in'))
);

CREATE TABLE "tide_events" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "title" TEXT COLLATE "C" NOT NULL,
    "details" TEXT COLLATE "C" NOT NULL,
    "location" TEXT COLLATE "C" NOT NULL,
    "starts_at" TEXT COLLATE "C" NOT NULL,
    "ends_at" TEXT COLLATE "C" NOT NULL,
    "time_zone" TEXT COLLATE "C" NOT NULL,
    "capacity" BIGINT NOT NULL,
    "state" TEXT COLLATE "C" NOT NULL,
    "version" BIGINT NOT NULL DEFAULT 0,
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_events" PRIMARY KEY ("id"),
    CONSTRAINT "pgck_tide_events_01" CHECK (capacity BETWEEN 1 AND 500),
    CONSTRAINT "pgck_tide_events_02" CHECK (state IN('draft','published','cancelled')),
    CONSTRAINT "pguq_tide_events_01" UNIQUE ("id", "tenant_id")
);

CREATE TABLE "tide_feature_migrations" (
    "version" BIGINT NOT NULL,
    "resource_name" TEXT COLLATE "C" NOT NULL,
    "sha256" TEXT COLLATE "C" NOT NULL,
    "applied_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_feature_migrations" PRIMARY KEY ("version"),
    CONSTRAINT "pguq_tide_feature_migrations_01" UNIQUE ("resource_name")
);

CREATE TABLE "tide_leads" (
    "id" TEXT COLLATE "C" NOT NULL,
    "name" TEXT COLLATE "C" NOT NULL,
    "business" TEXT COLLATE "C" NOT NULL,
    "email" TEXT COLLATE "C" NOT NULL,
    "phone" TEXT COLLATE "C" NOT NULL,
    "vertical" TEXT COLLATE "C" NOT NULL,
    "stage" TEXT COLLATE "C" NOT NULL,
    "notes" TEXT COLLATE "C" NOT NULL,
    "follow_up" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C",
    "version" BIGINT NOT NULL DEFAULT 0,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    "city" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "source" TEXT COLLATE "C" NOT NULL DEFAULT 'manual',
    "submitted_by" TEXT COLLATE "C",
    "next_action" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "assignee" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "referral_code" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "created_at" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "identity_hash" TEXT COLLATE "C" NOT NULL DEFAULT '',
    CONSTRAINT "pgpk_tide_leads" PRIMARY KEY ("id")
);

CREATE TABLE "tide_loyalty_benefits" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "kind" TEXT COLLATE "C" NOT NULL,
    "title" TEXT COLLATE "C" NOT NULL,
    "terms" TEXT COLLATE "C" NOT NULL,
    "cost" BIGINT NOT NULL DEFAULT 0,
    "audience" TEXT COLLATE "C" NOT NULL DEFAULT 'all',
    "target" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "starts" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "ends" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "active" BIGINT NOT NULL DEFAULT 0,
    "version" BIGINT NOT NULL DEFAULT 0,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_loyalty_benefits" PRIMARY KEY ("id")
);

CREATE TABLE "tide_loyalty_ledger" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "member_id" TEXT COLLATE "C" NOT NULL,
    "event_key" TEXT COLLATE "C" NOT NULL,
    "delta" BIGINT NOT NULL,
    "kind" TEXT COLLATE "C" NOT NULL,
    "reason" TEXT COLLATE "C" NOT NULL,
    "benefit_id" TEXT COLLATE "C",
    "state" TEXT COLLATE "C" NOT NULL DEFAULT 'recorded',
    "actor" TEXT COLLATE "C" NOT NULL,
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_loyalty_ledger" PRIMARY KEY ("id")
);

CREATE TABLE "tide_loyalty_members" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "user_id" TEXT COLLATE "C" NOT NULL,
    "email" TEXT COLLATE "C" NOT NULL,
    "name" TEXT COLLATE "C" NOT NULL,
    "birthday" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "interests_json" TEXT COLLATE "C" NOT NULL DEFAULT '[]',
    "personalized" BIGINT NOT NULL DEFAULT 0,
    "version" BIGINT NOT NULL DEFAULT 0,
    "joined_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    "source_campaign" TEXT COLLATE "C",
    CONSTRAINT "pgpk_tide_loyalty_members" PRIMARY KEY ("id")
);

CREATE TABLE "tide_loyalty_programs" (
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "settings_json" TEXT COLLATE "C" NOT NULL,
    "version" BIGINT NOT NULL DEFAULT 0,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_loyalty_programs" PRIMARY KEY ("tenant_id")
);

CREATE TABLE "tide_media_operations" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "kind" TEXT COLLATE "C" NOT NULL,
    "request_key" TEXT COLLATE "C" NOT NULL,
    "request_hash" TEXT COLLATE "C" NOT NULL,
    "media_id" TEXT COLLATE "C" NOT NULL,
    "object_key" TEXT COLLATE "C" NOT NULL,
    "state" TEXT COLLATE "C" NOT NULL,
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_media_operations" PRIMARY KEY ("id"),
    CONSTRAINT "pgck_tide_media_operations_01" CHECK (kind IN('photo','menu','video')),
    CONSTRAINT "pgck_tide_media_operations_02" CHECK (state IN('pending','ready','cleanup','removed')),
    CONSTRAINT "pguq_tide_media_operations_01" UNIQUE ("tenant_id", "kind", "request_key")
);

CREATE TABLE "tide_merchant_accounts" (
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "environment" TEXT COLLATE "C" NOT NULL,
    "account_id" TEXT COLLATE "C",
    "request_key" TEXT COLLATE "C" NOT NULL,
    "request_json" TEXT COLLATE "C" NOT NULL,
    "state" TEXT COLLATE "C" NOT NULL,
    "checked_at" TEXT COLLATE "C",
    "version" BIGINT NOT NULL DEFAULT 0,
    "lease_key" TEXT COLLATE "C",
    "lease_until" TEXT COLLATE "C",
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_merchant_accounts" PRIMARY KEY ("tenant_id", "environment"),
    CONSTRAINT "pgck_tide_merchant_accounts_01" CHECK (environment='test'),
    CONSTRAINT "pguq_tide_merchant_accounts_01" UNIQUE ("request_key"),
    CONSTRAINT "pguq_tide_merchant_accounts_02" UNIQUE ("environment", "account_id")
);

CREATE TABLE "tide_merchant_notification_inbox" (
    "context" TEXT COLLATE "C" NOT NULL,
    "environment" TEXT COLLATE "C" NOT NULL,
    "account_id" TEXT COLLATE "C" NOT NULL,
    "event_id" TEXT COLLATE "C" NOT NULL,
    "event_type" TEXT COLLATE "C" NOT NULL,
    "object_id" TEXT COLLATE "C" NOT NULL,
    "state" TEXT COLLATE "C" NOT NULL,
    "lease_key" TEXT COLLATE "C",
    "lease_until" TEXT COLLATE "C",
    "attempts" BIGINT NOT NULL DEFAULT 0,
    "last_error" TEXT COLLATE "C",
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_merchant_notification_inbox" PRIMARY KEY ("context", "environment", "account_id", "event_id"),
    CONSTRAINT "pgck_tide_merchant_notification_inbox_01" CHECK (context IN('snapshot','thin')),
    CONSTRAINT "pgck_tide_merchant_notification_inbox_02" CHECK (environment='test')
);

CREATE TABLE "tide_merchant_refunds" (
    "environment" TEXT COLLATE "C" NOT NULL,
    "account_id" TEXT COLLATE "C" NOT NULL,
    "refund_id" TEXT COLLATE "C" NOT NULL,
    "attempt_id" TEXT COLLATE "C" NOT NULL,
    "charge_id" TEXT COLLATE "C" NOT NULL,
    "intent_id" TEXT COLLATE "C" NOT NULL,
    "amount_cents" BIGINT NOT NULL,
    "status" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_merchant_refunds" PRIMARY KEY ("environment", "account_id", "refund_id"),
    CONSTRAINT "pgck_tide_merchant_refunds_01" CHECK (environment='test')
);

CREATE TABLE "tide_posts" (
    "id" TEXT COLLATE "C" NOT NULL,
    "scope" TEXT COLLATE "C" NOT NULL,
    "slug" TEXT COLLATE "C" NOT NULL,
    "title" TEXT COLLATE "C" NOT NULL,
    "excerpt" TEXT COLLATE "C" NOT NULL,
    "body" TEXT COLLATE "C" NOT NULL,
    "status" TEXT COLLATE "C" NOT NULL DEFAULT 'draft',
    "version" BIGINT NOT NULL DEFAULT 0,
    "published_at" TEXT COLLATE "C",
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_posts" PRIMARY KEY ("id")
);

CREATE TABLE "tide_push_outbox" (
    "post_id" TEXT COLLATE "C" NOT NULL,
    "subscription_id" TEXT COLLATE "C" NOT NULL,
    "subscription_generation" BIGINT NOT NULL,
    "state" TEXT COLLATE "C" NOT NULL,
    "attempts" BIGINT NOT NULL DEFAULT 0,
    "next_attempt_at" TEXT COLLATE "C" NOT NULL,
    "lease_key" TEXT COLLATE "C",
    "lease_until" TEXT COLLATE "C",
    "last_status" BIGINT,
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_push_outbox" PRIMARY KEY ("post_id", "subscription_id"),
    CONSTRAINT "pgck_tide_push_outbox_01" CHECK (state IN('pending','sending','sent','cancelled','failed'))
);

CREATE TABLE "tide_push_subscriptions" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "user_id" TEXT COLLATE "C" NOT NULL,
    "endpoint" TEXT COLLATE "C" NOT NULL,
    "endpoint_hash" TEXT COLLATE "C" NOT NULL,
    "p256dh" TEXT COLLATE "C" NOT NULL,
    "auth" TEXT COLLATE "C" NOT NULL,
    "active" BIGINT NOT NULL,
    "generation" BIGINT NOT NULL DEFAULT 0,
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_push_subscriptions" PRIMARY KEY ("id"),
    CONSTRAINT "pgck_tide_push_subscriptions_01" CHECK (active IN(0,1)),
    CONSTRAINT "pguq_tide_push_subscriptions_01" UNIQUE ("tenant_id", "endpoint_hash")
);

CREATE TABLE "tide_referral_profiles" (
    "id" TEXT COLLATE "C" NOT NULL,
    "user_id" TEXT COLLATE "C" NOT NULL,
    "email" TEXT COLLATE "C" NOT NULL,
    "name" TEXT COLLATE "C" NOT NULL,
    "introduction" TEXT COLLATE "C" NOT NULL,
    "status" TEXT COLLATE "C" NOT NULL DEFAULT 'pending',
    "code" TEXT COLLATE "C",
    "discount_percent" BIGINT NOT NULL DEFAULT 0,
    "terms_version" TEXT COLLATE "C" NOT NULL,
    "terms_accepted_at" TEXT COLLATE "C" NOT NULL,
    "review_token" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_referral_profiles" PRIMARY KEY ("id"),
    CONSTRAINT "pgck_tide_referral_profiles_01" CHECK (status IN ('pending','active','paused','declined')),
    CONSTRAINT "pgck_tide_referral_profiles_02" CHECK (discount_percent BETWEEN 0 AND 100),
    CONSTRAINT "pguq_tide_referral_profiles_01" UNIQUE ("user_id")
);

CREATE TABLE "tide_referral_reviews" (
    "id" TEXT COLLATE "C" NOT NULL,
    "profile_id" TEXT COLLATE "C" NOT NULL,
    "reviewer_id" TEXT COLLATE "C" NOT NULL,
    "status" TEXT COLLATE "C" NOT NULL,
    "code" TEXT COLLATE "C",
    "discount_percent" BIGINT NOT NULL,
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_referral_reviews" PRIMARY KEY ("id")
);

CREATE TABLE "tide_referral_sales" (
    "event_id" TEXT COLLATE "C" NOT NULL,
    "order_id" TEXT COLLATE "C" NOT NULL,
    "profile_id" TEXT COLLATE "C" NOT NULL,
    "kind" TEXT COLLATE "C" NOT NULL,
    "environment" TEXT COLLATE "C" NOT NULL,
    "gross_cents" BIGINT NOT NULL,
    "refunded_cents" BIGINT NOT NULL DEFAULT 0,
    "commission_cents" BIGINT NOT NULL,
    "source_revision" BIGINT NOT NULL DEFAULT 0,
    "paid_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_referral_sales" PRIMARY KEY ("event_id"),
    CONSTRAINT "pgck_tide_referral_sales_01" CHECK (kind IN ('initial','recurring')),
    CONSTRAINT "pgck_tide_referral_sales_02" CHECK (environment IN ('sandbox','live')),
    CONSTRAINT "pgck_tide_referral_sales_03" CHECK (gross_cents > 0),
    CONSTRAINT "pgck_tide_referral_sales_04" CHECK (refunded_cents >= 0 AND refunded_cents <= gross_cents),
    CONSTRAINT "pgck_tide_referral_sales_05" CHECK (commission_cents >= 0),
    CONSTRAINT "pgck_tide_referral_sales_06" CHECK (source_revision >= 0)
);

CREATE TABLE "tide_restaurant_payment_attempts" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "order_id" TEXT COLLATE "C" NOT NULL,
    "environment" TEXT COLLATE "C" NOT NULL,
    "account_id" TEXT COLLATE "C" NOT NULL,
    "request_hash" TEXT COLLATE "C" NOT NULL,
    "amount_cents" BIGINT NOT NULL,
    "currency" TEXT COLLATE "C" NOT NULL,
    "provider_request_json" TEXT COLLATE "C" NOT NULL,
    "state" TEXT COLLATE "C" NOT NULL,
    "session_id" TEXT COLLATE "C",
    "intent_id" TEXT COLLATE "C",
    "charge_id" TEXT COLLATE "C",
    "expires_at" TEXT COLLATE "C",
    "refunded_cents" BIGINT NOT NULL DEFAULT 0,
    "pending_refund_cents" BIGINT NOT NULL DEFAULT 0,
    "client_hash" TEXT COLLATE "C" NOT NULL,
    "version" BIGINT NOT NULL DEFAULT 0,
    "lease_key" TEXT COLLATE "C",
    "lease_until" TEXT COLLATE "C",
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_restaurant_payment_attempts" PRIMARY KEY ("id"),
    CONSTRAINT "pgck_tide_restaurant_payment_attempts_01" CHECK (environment='test'),
    CONSTRAINT "pgck_tide_restaurant_payment_attempts_02" CHECK (amount_cents>0),
    CONSTRAINT "pgck_tide_restaurant_payment_attempts_03" CHECK (currency='usd'),
    CONSTRAINT "pguq_tide_restaurant_payment_attempts_01" UNIQUE ("order_id"),
    CONSTRAINT "pguq_tide_restaurant_payment_attempts_02" UNIQUE ("environment", "account_id", "session_id"),
    CONSTRAINT "pguq_tide_restaurant_payment_attempts_03" UNIQUE ("environment", "account_id", "intent_id")
);

CREATE TABLE "tide_reward_commands" (
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "actor" TEXT COLLATE "C" NOT NULL,
    "request_id" TEXT COLLATE "C" NOT NULL,
    "request_hash" TEXT COLLATE "C" NOT NULL,
    "result_id" TEXT COLLATE "C" NOT NULL,
    "message" TEXT COLLATE "C" NOT NULL,
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_reward_commands" PRIMARY KEY ("tenant_id", "actor", "request_id")
);

CREATE TABLE "tide_reward_issued" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "member_id" TEXT COLLATE "C" NOT NULL,
    "version_id" TEXT COLLATE "C" NOT NULL,
    "period" TEXT COLLATE "C" NOT NULL,
    "ordinal" BIGINT NOT NULL,
    "state" TEXT COLLATE "C" NOT NULL,
    "points_cost" BIGINT NOT NULL DEFAULT 0,
    "earned_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    "resolution_note" TEXT COLLATE "C" NOT NULL DEFAULT '',
    CONSTRAINT "pgpk_tide_reward_issued" PRIMARY KEY ("id"),
    CONSTRAINT "pgck_tide_reward_issued_01" CHECK (ordinal>0),
    CONSTRAINT "pgck_tide_reward_issued_02" CHECK (state IN('available','requested','fulfilled','void','cancelled')),
    CONSTRAINT "pgck_tide_reward_issued_03" CHECK (points_cost>=0),
    CONSTRAINT "pguq_tide_reward_issued_01" UNIQUE ("member_id", "version_id", "period", "ordinal")
);

CREATE TABLE "tide_reward_qualifications" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "rule_id" TEXT COLLATE "C" NOT NULL,
    "version_id" TEXT COLLATE "C" NOT NULL,
    "member_id" TEXT COLLATE "C" NOT NULL,
    "source_key" TEXT COLLATE "C" NOT NULL,
    "source_kind" TEXT COLLATE "C" NOT NULL,
    "source_reference" TEXT COLLATE "C" NOT NULL,
    "units" BIGINT NOT NULL,
    "item_id" TEXT COLLATE "C",
    "local_day" TEXT COLLATE "C",
    "period" TEXT COLLATE "C" NOT NULL,
    "state" TEXT COLLATE "C" NOT NULL,
    "note" TEXT COLLATE "C" NOT NULL,
    "actor" TEXT COLLATE "C" NOT NULL,
    "occurred_at" TEXT COLLATE "C" NOT NULL,
    "void_reason" TEXT COLLATE "C",
    CONSTRAINT "pgpk_tide_reward_qualifications" PRIMARY KEY ("id"),
    CONSTRAINT "pgck_tide_reward_qualifications_01" CHECK (units>0),
    CONSTRAINT "pgck_tide_reward_qualifications_02" CHECK (state IN('eligible','void')),
    CONSTRAINT "pguq_tide_reward_qualifications_01" UNIQUE ("tenant_id", "rule_id", "source_key")
);

CREATE TABLE "tide_reward_rule_versions" (
    "id" TEXT COLLATE "C" NOT NULL,
    "rule_id" TEXT COLLATE "C" NOT NULL,
    "version" BIGINT NOT NULL,
    "title" TEXT COLLATE "C" NOT NULL,
    "reward" TEXT COLLATE "C" NOT NULL,
    "kind" TEXT COLLATE "C" NOT NULL,
    "threshold" BIGINT NOT NULL,
    "item_id" TEXT COLLATE "C",
    "timezone_id" TEXT COLLATE "C" NOT NULL,
    "active" BIGINT NOT NULL,
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_reward_rule_versions" PRIMARY KEY ("id"),
    CONSTRAINT "pgck_tide_reward_rule_versions_01" CHECK (kind IN('punch-card','monthly-visits','points')),
    CONSTRAINT "pgck_tide_reward_rule_versions_02" CHECK (threshold>0),
    CONSTRAINT "pgck_tide_reward_rule_versions_03" CHECK (active IN(0,1)),
    CONSTRAINT "pguq_tide_reward_rule_versions_01" UNIQUE ("rule_id", "version")
);

CREATE TABLE "tide_reward_rules" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "current_version_id" TEXT COLLATE "C" NOT NULL,
    "version" BIGINT NOT NULL DEFAULT 0,
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_reward_rules" PRIMARY KEY ("id")
);

CREATE TABLE "tide_sales_demo_links" (
    "request_id" TEXT COLLATE "C" NOT NULL,
    "lead_id" TEXT COLLATE "C" NOT NULL,
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_sales_demo_links" PRIMARY KEY ("request_id")
);

CREATE TABLE "tide_sales_history" (
    "id" TEXT COLLATE "C" NOT NULL,
    "lead_id" TEXT COLLATE "C" NOT NULL,
    "kind" TEXT COLLATE "C" NOT NULL,
    "summary" TEXT COLLATE "C" NOT NULL,
    "private_note" TEXT COLLATE "C" NOT NULL DEFAULT '',
    "actor" TEXT COLLATE "C" NOT NULL,
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_sales_history" PRIMARY KEY ("id"),
    CONSTRAINT "pgck_tide_sales_history_01" CHECK (kind IN ('created','updated','demo-linked'))
);

CREATE TABLE "tide_sales_operations" (
    "actor" TEXT COLLATE "C" NOT NULL,
    "request_key" TEXT COLLATE "C" NOT NULL,
    "operation" TEXT COLLATE "C" NOT NULL,
    "payload_hash" TEXT COLLATE "C" NOT NULL,
    "lead_id" TEXT COLLATE "C" NOT NULL,
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_sales_operations" PRIMARY KEY ("actor", "request_key")
);

CREATE TABLE "tide_schema_migrations" (
    "version" BIGINT NOT NULL,
    "resource_name" TEXT COLLATE "C" NOT NULL,
    "source_path" TEXT COLLATE "C" NOT NULL,
    "sha256" TEXT COLLATE "C" NOT NULL,
    "manifest_sha256" TEXT COLLATE "C" NOT NULL,
    "applied_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_schema_migrations" PRIMARY KEY ("version"),
    CONSTRAINT "pguq_tide_schema_migrations_01" UNIQUE ("resource_name")
);

CREATE TABLE "tide_service_event_inbox" (
    "environment" TEXT COLLATE "C" NOT NULL,
    "event_id" TEXT COLLATE "C" NOT NULL,
    "event_type" TEXT COLLATE "C" NOT NULL,
    "object_type" TEXT COLLATE "C" NOT NULL,
    "object_id" TEXT COLLATE "C" NOT NULL,
    "state" TEXT COLLATE "C" NOT NULL DEFAULT 'pending',
    "lease_token" TEXT COLLATE "C",
    "lease_until" BIGINT NOT NULL DEFAULT 0,
    "attempts" BIGINT NOT NULL DEFAULT 0,
    "failure_code" TEXT COLLATE "C",
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_service_event_inbox" PRIMARY KEY ("environment", "event_id"),
    CONSTRAINT "pgck_tide_service_event_inbox_01" CHECK (environment IN ('sandbox','live')),
    CONSTRAINT "pgck_tide_service_event_inbox_02" CHECK (state IN ('pending','processing','complete','ignored'))
);

CREATE TABLE "tide_service_events" (
    "id" TEXT COLLATE "C" NOT NULL,
    "order_id" TEXT COLLATE "C" NOT NULL,
    "event_type" TEXT COLLATE "C" NOT NULL,
    "processed_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_service_events" PRIMARY KEY ("id")
);

CREATE TABLE "tide_service_invoices" (
    "id" TEXT COLLATE "C" NOT NULL,
    "order_id" TEXT COLLATE "C" NOT NULL,
    "kind" TEXT COLLATE "C" NOT NULL,
    "amount_cents" BIGINT NOT NULL,
    "status" TEXT COLLATE "C" NOT NULL,
    "hosted_url" TEXT COLLATE "C",
    "paid_at" TEXT COLLATE "C",
    "refunded_cents" BIGINT NOT NULL DEFAULT 0,
    "refund_pending_cents" BIGINT NOT NULL DEFAULT 0,
    "refund_failed_cents" BIGINT NOT NULL DEFAULT 0,
    "refund_revision" BIGINT NOT NULL DEFAULT 0,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_service_invoices" PRIMARY KEY ("id"),
    CONSTRAINT "pgck_tide_service_invoices_01" CHECK (kind IN ('initial','recurring')),
    CONSTRAINT "pgck_tide_service_invoices_02" CHECK (amount_cents>=5000),
    CONSTRAINT "pgck_tide_service_invoices_03" CHECK (refunded_cents>=0),
    CONSTRAINT "pgck_tide_service_invoices_04" CHECK (refund_pending_cents>=0),
    CONSTRAINT "pgck_tide_service_invoices_05" CHECK (refund_failed_cents>=0)
);

CREATE TABLE "tide_service_orders" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "environment" TEXT COLLATE "C" NOT NULL,
    "status" TEXT COLLATE "C" NOT NULL DEFAULT 'pending',
    "request_json" TEXT COLLATE "C" NOT NULL,
    "initial_cents" BIGINT NOT NULL,
    "monthly_cents" BIGINT NOT NULL,
    "total_cents" BIGINT NOT NULL,
    "app_stores" BIGINT NOT NULL DEFAULT 0,
    "session_id" TEXT COLLATE "C",
    "subscription_id" TEXT COLLATE "C",
    "customer_id" TEXT COLLATE "C",
    "initial_invoice_id" TEXT COLLATE "C",
    "subscription_status" TEXT COLLATE "C" NOT NULL DEFAULT 'pending',
    "cancel_at_period_end" BIGINT NOT NULL DEFAULT 0,
    "period_end" BIGINT,
    "paid_at" TEXT COLLATE "C",
    "subscription_revision" BIGINT NOT NULL DEFAULT 0,
    "created_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_service_orders" PRIMARY KEY ("id"),
    CONSTRAINT "pgck_tide_service_orders_01" CHECK (environment IN ('sandbox','live')),
    CONSTRAINT "pgck_tide_service_orders_02" CHECK (status IN ('pending','processing','paid','expired','failed')),
    CONSTRAINT "pgck_tide_service_orders_03" CHECK (initial_cents BETWEEN 0 AND 90000),
    CONSTRAINT "pgck_tide_service_orders_04" CHECK (monthly_cents=5000),
    CONSTRAINT "pgck_tide_service_orders_05" CHECK (total_cents=initial_cents+monthly_cents),
    CONSTRAINT "pgck_tide_service_orders_06" CHECK (app_stores IN (0,1)),
    CONSTRAINT "pguq_tide_service_orders_01" UNIQUE ("session_id"),
    CONSTRAINT "pguq_tide_service_orders_02" UNIQUE ("subscription_id"),
    CONSTRAINT "pguq_tide_service_orders_03" UNIQUE ("initial_invoice_id")
);

CREATE TABLE "tide_service_refund_sync" (
    "invoice_id" TEXT COLLATE "C" NOT NULL,
    "charge_id" TEXT COLLATE "C" NOT NULL,
    "revision" BIGINT NOT NULL DEFAULT 0,
    "token" TEXT COLLATE "C" NOT NULL DEFAULT '',
    CONSTRAINT "pgpk_tide_service_refund_sync" PRIMARY KEY ("invoice_id")
);

CREATE TABLE "tide_service_refunds" (
    "id" TEXT COLLATE "C" NOT NULL,
    "invoice_id" TEXT COLLATE "C" NOT NULL,
    "charge_id" TEXT COLLATE "C" NOT NULL,
    "amount_cents" BIGINT NOT NULL,
    "status" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_service_refunds" PRIMARY KEY ("id"),
    CONSTRAINT "pgck_tide_service_refunds_01" CHECK (amount_cents>0),
    CONSTRAINT "pgck_tide_service_refunds_02" CHECK (status IN ('pending','requires_action','succeeded','failed','canceled'))
);

CREATE TABLE "tide_staff_course_assignments" (
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "member_id" TEXT COLLATE "C" NOT NULL,
    "course_id" TEXT COLLATE "C" NOT NULL,
    "active" BIGINT NOT NULL DEFAULT 1,
    "version" BIGINT NOT NULL DEFAULT 0,
    "assigned_at" TEXT COLLATE "C" NOT NULL,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_staff_course_assignments" PRIMARY KEY ("member_id", "course_id"),
    CONSTRAINT "pgck_tide_staff_course_assignments_01" CHECK (active IN (0,1))
);

CREATE TABLE "tide_staff_lesson_progress" (
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "member_id" TEXT COLLATE "C" NOT NULL,
    "lesson_id" TEXT COLLATE "C" NOT NULL,
    "completed" BIGINT NOT NULL DEFAULT 0,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_staff_lesson_progress" PRIMARY KEY ("member_id", "lesson_id"),
    CONSTRAINT "pgck_tide_staff_lesson_progress_01" CHECK (completed IN (0,1))
);

CREATE TABLE "tide_support" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "author_id" TEXT COLLATE "C" NOT NULL,
    "author" TEXT COLLATE "C" NOT NULL,
    "from_owner" BIGINT NOT NULL,
    "body" TEXT COLLATE "C" NOT NULL,
    "read_at" TEXT COLLATE "C",
    "created_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_support" PRIMARY KEY ("id")
);

CREATE TABLE "tide_tasks" (
    "id" TEXT COLLATE "C" NOT NULL,
    "tenant_id" TEXT COLLATE "C" NOT NULL,
    "title" TEXT COLLATE "C" NOT NULL,
    "done" BIGINT NOT NULL DEFAULT 0,
    "due" TEXT COLLATE "C" NOT NULL,
    "version" BIGINT NOT NULL DEFAULT 0,
    "updated_at" TEXT COLLATE "C" NOT NULL,
    CONSTRAINT "pgpk_tide_tasks" PRIMARY KEY ("id")
);

-- Existing named and partial indexes, plus exact ASCII-NOCASE uniqueness.
CREATE INDEX "idx_auth_legacy_user" ON "bartide_auth_legacy_claims" ("legacy_user_id");
CREATE INDEX "idx_auth_sessions_expiry" ON "bartide_auth_sessions" ("expires_at");
CREATE INDEX "idx_auth_sessions_user" ON "bartide_auth_sessions" ("provider_user_id");
CREATE INDEX "idx_bartide_campaign_tenant" ON "bartide_campaigns" ("tenant_id");
CREATE INDEX "idx_bartide_contact_phone" ON "bartide_contact_details" ("tenant_id", "phone");
CREATE UNIQUE INDEX "idx_bartide_customer_email" ON "bartide_customers" ("email");
CREATE UNIQUE INDEX "idx_bartide_customer_slug" ON "bartide_customers" ("slug");
CREATE INDEX "idx_bartide_customer_user" ON "bartide_customers" ("user_id");
CREATE UNIQUE INDEX "idx_enhanced_member_email" ON "bartide_enhanced_members" ("tenant_id", "email");
CREATE INDEX "idx_enhanced_member_user" ON "bartide_enhanced_members" ("user_id");
CREATE INDEX "idx_enhanced_message_time" ON "bartide_enhanced_messages" ("tenant_id", "created_at");
CREATE INDEX "idx_enhanced_order_queue" ON "bartide_enhanced_orders" ("tenant_id", "status", "created_at");
CREATE UNIQUE INDEX "idx_enhanced_order_request" ON "bartide_enhanced_orders" ("tenant_id", "request_key");
CREATE INDEX "idx_enhanced_shift_member" ON "bartide_enhanced_shifts" ("tenant_id", "member_id", "starts_at");
CREATE INDEX "idx_bartide_growth_event_tenant" ON "bartide_growth_events" ("tenant_id", "created_at");
CREATE UNIQUE INDEX "idx_bartide_pass_hash" ON "bartide_member_passes" ("token_hash");
CREATE INDEX "idx_bartide_menu_files_tenant" ON "bartide_menu_files" ("tenant_id");
CREATE UNIQUE INDEX "idx_bartide_payment_active" ON "bartide_payment_orders" ("tenant_id", "environment") WHERE status NOT IN ('failed','expired','void');
CREATE INDEX "idx_bartide_payment_tenant" ON "bartide_payment_orders" ("tenant_id", "created_at");
CREATE INDEX "idx_bartide_refunds_order" ON "bartide_payment_refunds" ("order_id");
CREATE INDEX "idx_bartide_photos_tenant" ON "bartide_photos" ("tenant_id");
CREATE UNIQUE INDEX "idx_bartide_sms_provider" ON "bartide_sms_messages" ("provider_sid");
CREATE INDEX "idx_bartide_sms_tenant" ON "bartide_sms_messages" ("tenant_id", "created_at");
CREATE INDEX "demo_requests_email_time" ON "demo_requests" ("email_hash", "created_at");
CREATE INDEX "idx_fit_courses_tenant" ON "fit_courses" ("tenant_id", "created_at");
CREATE UNIQUE INDEX "idx_fit_learners_email" ON "fit_learners" ("tenant_id", "email");
CREATE INDEX "idx_fit_learners_user" ON "fit_learners" ("user_id");
CREATE INDEX "idx_fit_lessons_course" ON "fit_lessons" ("tenant_id", "course_id", "position");
CREATE UNIQUE INDEX "idx_fit_progress_lesson" ON "fit_progress" ("learner_id", "lesson_id");
CREATE INDEX "idx_fit_videos_tenant" ON "fit_videos" ("tenant_id", "status");
CREATE INDEX "idx_business_posts_feed" ON "tide_business_posts" ("tenant_id", "state", "published_at");
CREATE INDEX "idx_contact_created" ON "tide_contact_inquiries" ("created_at");
CREATE INDEX "idx_contact_email_created" ON "tide_contact_inquiries" ("email_hash", "created_at");
CREATE INDEX "idx_contact_ip_created" ON "tide_contact_inquiries" ("ip_hash", "created_at");
CREATE INDEX "idx_contact_notification" ON "tide_contact_inquiries" ("notification_state", "next_attempt_at");
CREATE INDEX "idx_demo_notifications_due" ON "tide_demo_notifications" ("state", "next_attempt_at");
CREATE INDEX "idx_employee_reward_member" ON "tide_employee_reward_ledger" ("tenant_id", "member_id");
CREATE INDEX "idx_events_tenant" ON "tide_events" ("tenant_id", "state", "starts_at");
CREATE INDEX "idx_tide_leads_stage" ON "tide_leads" ("stage", "updated_at");
CREATE INDEX "tide_sales_follow_up" ON "tide_leads" ("follow_up", "stage");
CREATE INDEX "tide_sales_identity" ON "tide_leads" ("identity_hash");
CREATE INDEX "idx_loyalty_benefit_tenant" ON "tide_loyalty_benefits" ("tenant_id", "kind", "active");
CREATE UNIQUE INDEX "idx_loyalty_ledger_event" ON "tide_loyalty_ledger" ("member_id", "event_key");
CREATE INDEX "idx_loyalty_ledger_member" ON "tide_loyalty_ledger" ("member_id", "created_at");
CREATE INDEX "idx_loyalty_ledger_tenant" ON "tide_loyalty_ledger" ("tenant_id", "created_at");
CREATE INDEX "idx_loyalty_member_identity" ON "tide_loyalty_members" ("user_id");
CREATE UNIQUE INDEX "idx_loyalty_member_user" ON "tide_loyalty_members" ("tenant_id", "user_id");
CREATE UNIQUE INDEX "tide_media_operation_object" ON "tide_media_operations" ("object_key");
CREATE INDEX "tide_media_operation_pending" ON "tide_media_operations" ("state", "updated_at");
CREATE UNIQUE INDEX "idx_tide_posts_slug" ON "tide_posts" ("scope", "slug");
CREATE INDEX "idx_tide_posts_status" ON "tide_posts" ("scope", "status", "updated_at");
CREATE INDEX "idx_push_outbox_dispatch" ON "tide_push_outbox" ("state", "next_attempt_at", "lease_until");
CREATE INDEX "idx_push_subscription_owner" ON "tide_push_subscriptions" ("tenant_id", "user_id", "active");
CREATE INDEX "idx_referral_profiles_status" ON "tide_referral_profiles" ("status", "created_at");
CREATE UNIQUE INDEX "pgux_tide_referral_profiles_email_nocase" ON "tide_referral_profiles" (translate("email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') COLLATE "C");
CREATE UNIQUE INDEX "pgux_tide_referral_profiles_code_nocase" ON "tide_referral_profiles" (translate("code", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') COLLATE "C");
CREATE INDEX "idx_referral_sales_profile" ON "tide_referral_sales" ("profile_id", "environment", "paid_at");
CREATE INDEX "idx_restaurant_payment_client" ON "tide_restaurant_payment_attempts" ("tenant_id", "client_hash", "state");
CREATE INDEX "idx_restaurant_payment_recovery" ON "tide_restaurant_payment_attempts" ("state", "updated_at");
CREATE INDEX "idx_reward_issued_tenant" ON "tide_reward_issued" ("tenant_id", "member_id", "state");
CREATE INDEX "idx_reward_qualification_member" ON "tide_reward_qualifications" ("tenant_id", "member_id", "version_id");
CREATE UNIQUE INDEX "idx_reward_visit_day" ON "tide_reward_qualifications" ("rule_id", "member_id", "local_day") WHERE local_day IS NOT NULL;
CREATE INDEX "idx_reward_rules_tenant" ON "tide_reward_rules" ("tenant_id");
CREATE INDEX "tide_sales_demo_lead" ON "tide_sales_demo_links" ("lead_id", "created_at");
CREATE INDEX "tide_sales_history_lead" ON "tide_sales_history" ("lead_id", "created_at", "id");
CREATE INDEX "tide_service_inbox_pending" ON "tide_service_event_inbox" ("state", "lease_until", "updated_at");
CREATE INDEX "tide_service_invoice_order" ON "tide_service_invoices" ("order_id");
CREATE UNIQUE INDEX "tide_service_active" ON "tide_service_orders" ("tenant_id", "environment") WHERE status NOT IN ('expired','failed');
CREATE INDEX "tide_staff_assignments_tenant" ON "tide_staff_course_assignments" ("tenant_id", "course_id", "active");
CREATE INDEX "idx_tide_support_tenant" ON "tide_support" ("tenant_id", "created_at");
CREATE INDEX "idx_tide_tasks_tenant" ON "tide_tasks" ("tenant_id", "updated_at");

-- Apply references after every table exists; legacy SQLite DDL contains forward references.
ALTER TABLE "bartide_auth_sessions" ADD CONSTRAINT "pgfk_bartide_auth_sessions_01" FOREIGN KEY ("provider_user_id") REFERENCES "bartide_auth_identities" ("provider_user_id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_campaigns" ADD CONSTRAINT "pgfk_bartide_campaigns_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_connections" ADD CONSTRAINT "pgfk_bartide_connections_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_contact_details" ADD CONSTRAINT "pgfk_bartide_contact_details_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_contact_details" ADD CONSTRAINT "pgfk_bartide_contact_details_02" FOREIGN KEY ("member_id") REFERENCES "tide_loyalty_members" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_enhanced_configs" ADD CONSTRAINT "pgfk_bartide_enhanced_configs_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_enhanced_members" ADD CONSTRAINT "pgfk_bartide_enhanced_members_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_enhanced_messages" ADD CONSTRAINT "pgfk_bartide_enhanced_messages_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_enhanced_orders" ADD CONSTRAINT "pgfk_bartide_enhanced_orders_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_enhanced_shifts" ADD CONSTRAINT "pgfk_bartide_enhanced_shifts_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_enhanced_shifts" ADD CONSTRAINT "pgfk_bartide_enhanced_shifts_02" FOREIGN KEY ("member_id") REFERENCES "bartide_enhanced_members" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_growth_events" ADD CONSTRAINT "pgfk_bartide_growth_events_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_growth_events" ADD CONSTRAINT "pgfk_bartide_growth_events_02" FOREIGN KEY ("member_id") REFERENCES "tide_loyalty_members" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_member_passes" ADD CONSTRAINT "pgfk_bartide_member_passes_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_member_passes" ADD CONSTRAINT "pgfk_bartide_member_passes_02" FOREIGN KEY ("member_id") REFERENCES "tide_loyalty_members" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_menu_files" ADD CONSTRAINT "pgfk_bartide_menu_files_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_payment_events" ADD CONSTRAINT "pgfk_bartide_payment_events_01" FOREIGN KEY ("order_id") REFERENCES "bartide_payment_orders" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_payment_orders" ADD CONSTRAINT "pgfk_bartide_payment_orders_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_payment_refund_sync" ADD CONSTRAINT "pgfk_bartide_payment_refund_sync_01" FOREIGN KEY ("order_id") REFERENCES "bartide_payment_orders" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_payment_refunds" ADD CONSTRAINT "pgfk_bartide_payment_refunds_01" FOREIGN KEY ("order_id") REFERENCES "bartide_payment_orders" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_photos" ADD CONSTRAINT "pgfk_bartide_photos_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_sms_messages" ADD CONSTRAINT "pgfk_bartide_sms_messages_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "bartide_sms_messages" ADD CONSTRAINT "pgfk_bartide_sms_messages_02" FOREIGN KEY ("member_id") REFERENCES "tide_loyalty_members" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "fit_courses" ADD CONSTRAINT "pgfk_fit_courses_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "fit_learners" ADD CONSTRAINT "pgfk_fit_learners_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "fit_lessons" ADD CONSTRAINT "pgfk_fit_lessons_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "fit_lessons" ADD CONSTRAINT "pgfk_fit_lessons_02" FOREIGN KEY ("course_id") REFERENCES "fit_courses" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "fit_progress" ADD CONSTRAINT "pgfk_fit_progress_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "fit_progress" ADD CONSTRAINT "pgfk_fit_progress_02" FOREIGN KEY ("learner_id") REFERENCES "fit_learners" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "fit_progress" ADD CONSTRAINT "pgfk_fit_progress_03" FOREIGN KEY ("lesson_id") REFERENCES "fit_lessons" ("id") ON UPDATE NO ACTION ON DELETE CASCADE DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "fit_videos" ADD CONSTRAINT "pgfk_fit_videos_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_business_post_requests" ADD CONSTRAINT "pgfk_tide_business_post_requests_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_business_post_requests" ADD CONSTRAINT "pgfk_tide_business_post_requests_02" FOREIGN KEY ("post_id") REFERENCES "tide_business_posts" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_business_posts" ADD CONSTRAINT "pgfk_tide_business_posts_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_demo_inbox" ADD CONSTRAINT "pgfk_tide_demo_inbox_01" FOREIGN KEY ("request_id") REFERENCES "demo_requests" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_demo_notifications" ADD CONSTRAINT "pgfk_tide_demo_notifications_01" FOREIGN KEY ("request_id") REFERENCES "demo_requests" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_document_reviews" ADD CONSTRAINT "pgfk_tide_document_reviews_01" FOREIGN KEY ("file_id") REFERENCES "bartide_menu_files" ("id") ON UPDATE NO ACTION ON DELETE CASCADE DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_employee_reward_ledger" ADD CONSTRAINT "pgfk_tide_employee_reward_ledger_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_employee_reward_ledger" ADD CONSTRAINT "pgfk_tide_employee_reward_ledger_02" FOREIGN KEY ("member_id") REFERENCES "bartide_enhanced_members" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_event_audit" ADD CONSTRAINT "pgfk_tide_event_audit_01" FOREIGN KEY ("event_id") REFERENCES "tide_events" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_event_rsvp_requests" ADD CONSTRAINT "pgfk_tide_event_rsvp_requests_01" FOREIGN KEY ("event_id", "user_id") REFERENCES "tide_event_rsvps" ("event_id", "user_id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_event_rsvps" ADD CONSTRAINT "pgfk_tide_event_rsvps_01" FOREIGN KEY ("event_id") REFERENCES "tide_events" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_events" ADD CONSTRAINT "pgfk_tide_events_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_leads" ADD CONSTRAINT "pgfk_tide_leads_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_loyalty_benefits" ADD CONSTRAINT "pgfk_tide_loyalty_benefits_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_loyalty_ledger" ADD CONSTRAINT "pgfk_tide_loyalty_ledger_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_loyalty_ledger" ADD CONSTRAINT "pgfk_tide_loyalty_ledger_02" FOREIGN KEY ("benefit_id") REFERENCES "tide_loyalty_benefits" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_loyalty_ledger" ADD CONSTRAINT "pgfk_tide_loyalty_ledger_03" FOREIGN KEY ("member_id") REFERENCES "tide_loyalty_members" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_loyalty_members" ADD CONSTRAINT "pgfk_tide_loyalty_members_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_loyalty_programs" ADD CONSTRAINT "pgfk_tide_loyalty_programs_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_media_operations" ADD CONSTRAINT "pgfk_tide_media_operations_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_merchant_accounts" ADD CONSTRAINT "pgfk_tide_merchant_accounts_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_merchant_refunds" ADD CONSTRAINT "pgfk_tide_merchant_refunds_01" FOREIGN KEY ("attempt_id") REFERENCES "tide_restaurant_payment_attempts" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_push_outbox" ADD CONSTRAINT "pgfk_tide_push_outbox_01" FOREIGN KEY ("post_id") REFERENCES "tide_business_posts" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_push_outbox" ADD CONSTRAINT "pgfk_tide_push_outbox_02" FOREIGN KEY ("subscription_id") REFERENCES "tide_push_subscriptions" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_push_subscriptions" ADD CONSTRAINT "pgfk_tide_push_subscriptions_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_referral_reviews" ADD CONSTRAINT "pgfk_tide_referral_reviews_01" FOREIGN KEY ("profile_id") REFERENCES "tide_referral_profiles" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_referral_sales" ADD CONSTRAINT "pgfk_tide_referral_sales_01" FOREIGN KEY ("profile_id") REFERENCES "tide_referral_profiles" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_restaurant_payment_attempts" ADD CONSTRAINT "pgfk_tide_restaurant_payment_attempts_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_restaurant_payment_attempts" ADD CONSTRAINT "pgfk_tide_restaurant_payment_attempts_02" FOREIGN KEY ("order_id") REFERENCES "bartide_enhanced_orders" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_reward_commands" ADD CONSTRAINT "pgfk_tide_reward_commands_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_reward_issued" ADD CONSTRAINT "pgfk_tide_reward_issued_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_reward_issued" ADD CONSTRAINT "pgfk_tide_reward_issued_02" FOREIGN KEY ("member_id") REFERENCES "tide_loyalty_members" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_reward_issued" ADD CONSTRAINT "pgfk_tide_reward_issued_03" FOREIGN KEY ("version_id") REFERENCES "tide_reward_rule_versions" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_reward_qualifications" ADD CONSTRAINT "pgfk_tide_reward_qualifications_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_reward_qualifications" ADD CONSTRAINT "pgfk_tide_reward_qualifications_02" FOREIGN KEY ("member_id") REFERENCES "tide_loyalty_members" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_reward_qualifications" ADD CONSTRAINT "pgfk_tide_reward_qualifications_03" FOREIGN KEY ("version_id") REFERENCES "tide_reward_rule_versions" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_reward_qualifications" ADD CONSTRAINT "pgfk_tide_reward_qualifications_04" FOREIGN KEY ("rule_id") REFERENCES "tide_reward_rules" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_reward_rule_versions" ADD CONSTRAINT "pgfk_tide_reward_rule_versions_01" FOREIGN KEY ("rule_id") REFERENCES "tide_reward_rules" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_reward_rules" ADD CONSTRAINT "pgfk_tide_reward_rules_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_sales_demo_links" ADD CONSTRAINT "pgfk_tide_sales_demo_links_01" FOREIGN KEY ("request_id") REFERENCES "demo_requests" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_sales_demo_links" ADD CONSTRAINT "pgfk_tide_sales_demo_links_02" FOREIGN KEY ("lead_id") REFERENCES "tide_leads" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_sales_history" ADD CONSTRAINT "pgfk_tide_sales_history_01" FOREIGN KEY ("lead_id") REFERENCES "tide_leads" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_sales_operations" ADD CONSTRAINT "pgfk_tide_sales_operations_01" FOREIGN KEY ("lead_id") REFERENCES "tide_leads" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_service_events" ADD CONSTRAINT "pgfk_tide_service_events_01" FOREIGN KEY ("order_id") REFERENCES "tide_service_orders" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_service_invoices" ADD CONSTRAINT "pgfk_tide_service_invoices_01" FOREIGN KEY ("order_id") REFERENCES "tide_service_orders" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_service_orders" ADD CONSTRAINT "pgfk_tide_service_orders_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_service_refund_sync" ADD CONSTRAINT "pgfk_tide_service_refund_sync_01" FOREIGN KEY ("invoice_id") REFERENCES "tide_service_invoices" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_service_refunds" ADD CONSTRAINT "pgfk_tide_service_refunds_01" FOREIGN KEY ("invoice_id") REFERENCES "tide_service_invoices" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_staff_course_assignments" ADD CONSTRAINT "pgfk_tide_staff_course_assignments_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_staff_course_assignments" ADD CONSTRAINT "pgfk_tide_staff_course_assignments_02" FOREIGN KEY ("member_id") REFERENCES "bartide_enhanced_members" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_staff_course_assignments" ADD CONSTRAINT "pgfk_tide_staff_course_assignments_03" FOREIGN KEY ("course_id") REFERENCES "fit_courses" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_staff_lesson_progress" ADD CONSTRAINT "pgfk_tide_staff_lesson_progress_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_staff_lesson_progress" ADD CONSTRAINT "pgfk_tide_staff_lesson_progress_02" FOREIGN KEY ("member_id") REFERENCES "bartide_enhanced_members" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_staff_lesson_progress" ADD CONSTRAINT "pgfk_tide_staff_lesson_progress_03" FOREIGN KEY ("lesson_id") REFERENCES "fit_lessons" ("id") ON UPDATE NO ACTION ON DELETE CASCADE DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_support" ADD CONSTRAINT "pgfk_tide_support_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE "tide_tasks" ADD CONSTRAINT "pgfk_tide_tasks_01" FOREIGN KEY ("tenant_id") REFERENCES "bartide_customers" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY IMMEDIATE;


-- These parsing helpers never change the preserved text columns. No dynamic SQL or elevated privileges.
-- Restrict timestamps to ISO dates/explicit-offset instants before the PostgreSQL cast:
-- PostgreSQL's special values (infinity, tomorrow, now, etc.) are deliberately not accepted.
CREATE FUNCTION tide_iso_instant(input_text TEXT) RETURNS TIMESTAMPTZ
LANGUAGE plpgsql STABLE STRICT PARALLEL SAFE SECURITY INVOKER
SET search_path = pg_catalog
AS $tide_iso$
BEGIN
    IF input_text ~ '^[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])$' THEN
        RETURN (input_text || 'T00:00:00Z')::TIMESTAMPTZ;
    END IF;
    IF input_text !~ '^[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])[Tt ](0[0-9]|1[0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]{1,7})?([Zz]|[+-]((0[0-9]|1[0-3]):[0-5][0-9]|14:00))$' THEN
        RETURN NULL;
    END IF;
    RETURN input_text::TIMESTAMPTZ;
EXCEPTION WHEN data_exception THEN
    RETURN NULL;
END;
$tide_iso$;

-- JSONB parsing is deterministic; invalid JSON, unsupported escapes and out-of-range
-- JSON numbers yield SQL NULL. The valid JSON literal null remains JSONB null.
CREATE FUNCTION tide_json(input_text TEXT) RETURNS JSONB
LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE SECURITY INVOKER
SET search_path = pg_catalog
AS $tide_json$
BEGIN
    RETURN input_text::JSONB;
EXCEPTION WHEN data_exception THEN
    RETURN NULL;
END;
$tide_json$;
