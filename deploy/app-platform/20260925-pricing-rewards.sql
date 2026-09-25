-- BarTide pricing v3: run as the schema/table owner in the Supabase SQL editor.
-- Existing application schema only; no customer, subscription or payment data is edited.
-- Coordinate with the new API deployment. The old API cannot restart after ledger v3.
-- This is a fallback for the restricted runtime role. Prefer the compiled API's
-- --Storage:MigrateOnly=true with a temporary owner connection when available.
-- Exact embedded migration SHA-256: 8823457cc99016d54d4dcfd88742dd2d897c5bfbb8ceae2ae0ca8611486db716
-- Rehearsed on isolated PostgreSQL 17: 20 checks passed, including native hash parity,
-- idempotency, drift rejection and restricted-role Production startup over verified TLS.
BEGIN;
SET LOCAL search_path = tide_casa;
SET LOCAL lock_timeout = '10s';
SET LOCAL statement_timeout = '120s';
SET LOCAL TIME ZONE 'UTC';
SELECT pg_advisory_xact_lock(hashtext(current_database()),hashtext(current_schema()));

-- Reproduce System.Text.Json's default ASCII string encoding, including its
-- HTML-sensitive escapes. Catalog metadata is ASCII in this release. Reject
-- non-ASCII metadata instead of guessing a fingerprint for unexpected drift.
-- Functions exist only in this session's temporary schema, never in tide_casa.
CREATE OR REPLACE FUNCTION pg_temp.tide_pricing_json_string(input_value text)
RETURNS text LANGUAGE plpgsql IMMUTABLE AS $fn$
DECLARE output_value text := '"'; symbol text; code integer;
BEGIN
 FOR symbol IN SELECT regexp_split_to_table(COALESCE(input_value,''),'') LOOP
  IF symbol='' THEN CONTINUE; END IF;
  code := ascii(symbol);
  IF code>127 THEN RAISE EXCEPTION 'Non-ASCII catalog metadata requires the compiled migrator'; END IF;
  IF code IN (8,9,10,12,13) THEN
   output_value := output_value || chr(92) || CASE code WHEN 8 THEN 'b' WHEN 9 THEN 't' WHEN 10 THEN 'n' WHEN 12 THEN 'f' ELSE 'r' END;
  ELSIF code=92 THEN output_value := output_value || chr(92) || chr(92);
  ELSIF code<32 OR code IN (34,38,39,43,60,62,96,127) THEN
   output_value := output_value || chr(92) || 'u' || upper(lpad(to_hex(code),4,'0'));
  ELSE output_value := output_value || symbol;
  END IF;
 END LOOP;
 RETURN output_value || '"';
END $fn$;

CREATE OR REPLACE FUNCTION pg_temp.tide_pricing_json_row(parts text[])
RETURNS text LANGUAGE sql IMMUTABLE AS $fn$
 SELECT '[' || string_agg(pg_temp.tide_pricing_json_string(part),',' ORDER BY ord) || ']'
 FROM unnest(parts) WITH ORDINALITY AS items(part,ord)
$fn$;

-- Same six catalog queries and baseline inventory as PostgresSchemaMigrator.
-- Added driver/analytics tables are outside that inventory and remain untouched.
CREATE OR REPLACE FUNCTION pg_temp.tide_pricing_schema_hash()
RETURNS text LANGUAGE plpgsql STABLE AS $fn$
DECLARE
 tracked_tables text[] := ARRAY['bartide_auth_identities','bartide_auth_legacy_claims','bartide_auth_limits','bartide_auth_sessions','bartide_campaigns','bartide_connections','bartide_contact_details','bartide_customers','bartide_enhanced_configs','bartide_enhanced_limits','bartide_enhanced_members','bartide_enhanced_messages','bartide_enhanced_orders','bartide_enhanced_shifts','bartide_growth_events','bartide_member_passes','bartide_menu_files','bartide_payment_events','bartide_payment_orders','bartide_payment_refund_sync','bartide_payment_refunds','bartide_photos','bartide_sms_messages','demo_requests','fit_courses','fit_learners','fit_lessons','fit_progress','fit_videos','tide_business_post_requests','tide_business_posts','tide_contact_inquiries','tide_data_protection_keys','tide_demo_inbox','tide_demo_notifications','tide_document_reviews','tide_employee_reward_ledger','tide_event_audit','tide_event_rsvp_requests','tide_event_rsvps','tide_events','tide_feature_migrations','tide_leads','tide_loyalty_benefits','tide_loyalty_ledger','tide_loyalty_members','tide_loyalty_programs','tide_media_operations','tide_merchant_accounts','tide_merchant_notification_inbox','tide_merchant_refunds','tide_posts','tide_push_outbox','tide_push_subscriptions','tide_referral_profiles','tide_referral_reviews','tide_referral_sales','tide_restaurant_payment_attempts','tide_reward_commands','tide_reward_issued','tide_reward_qualifications','tide_reward_rule_versions','tide_reward_rules','tide_sales_demo_links','tide_sales_history','tide_sales_operations','tide_schema_migrations','tide_service_event_inbox','tide_service_events','tide_service_invoices','tide_service_orders','tide_service_refund_sync','tide_service_refunds','tide_staff_course_assignments','tide_staff_lesson_progress','tide_support','tide_tasks','tide_postgres_migrations'];
 tracked_functions text[] := ARRAY['tide_iso_instant','tide_json'];
BEGIN
 RETURN (
 WITH metadata AS (
SELECT ARRAY['table',c.relname::text,c.relkind::text,c.relrowsecurity::text,c.relforcerowsecurity::text,c.relpersistence::text] AS fields
FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname=current_schema() AND c.relkind IN ('r','p','v','m','f') AND c.relname=ANY(tracked_tables)
UNION ALL
SELECT ARRAY['column',c.relname::text,a.attnum::text,a.attname::text,pg_catalog.format_type(a.atttypid,a.atttypmod),
    a.attnotnull::text,COALESCE(pg_catalog.pg_get_expr(d.adbin,d.adrelid),''),
    COALESCE(coll.collname::text,''),a.attidentity::text,a.attgenerated::text] AS fields
FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_class c ON c.oid=a.attrelid
JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
LEFT JOIN pg_catalog.pg_collation coll ON coll.oid=a.attcollation
WHERE n.nspname=current_schema() AND a.attnum>0 AND NOT a.attisdropped AND c.relname=ANY(tracked_tables)
UNION ALL
SELECT ARRAY['constraint',c.relname::text,k.conname::text,k.contype::text,pg_catalog.pg_get_constraintdef(k.oid,true),
    k.convalidated::text,k.condeferrable::text,k.condeferred::text] AS fields
FROM pg_catalog.pg_constraint k JOIN pg_catalog.pg_class c ON c.oid=k.conrelid
JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname=current_schema() AND k.contype IN ('p','u','c','f','x') AND c.relname=ANY(tracked_tables)
UNION ALL
SELECT ARRAY['index',c.relname::text,i.relname::text,x.indisunique::text,x.indisprimary::text,
    x.indisvalid::text,x.indisready::text,pg_catalog.pg_get_indexdef(i.oid)] AS fields
FROM pg_catalog.pg_index x JOIN pg_catalog.pg_class c ON c.oid=x.indrelid
JOIN pg_catalog.pg_class i ON i.oid=x.indexrelid
JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname=current_schema() AND c.relname=ANY(tracked_tables)
UNION ALL
SELECT ARRAY['trigger',c.relname::text,t.tgname::text,t.tgenabled::text,pg_catalog.pg_get_triggerdef(t.oid,true)] AS fields
FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid=t.tgrelid
JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname=current_schema() AND NOT t.tgisinternal AND c.relname=ANY(tracked_tables)
UNION ALL
SELECT ARRAY['function',p.proname::text,pg_catalog.oidvectortypes(p.proargtypes),pg_catalog.pg_get_function_result(p.oid),
    p.provolatile::text,p.proisstrict::text,p.prosecdef::text,p.proparallel::text,
    COALESCE(array_to_string(p.proconfig,','),''),pg_catalog.pg_get_functiondef(p.oid)] AS fields
FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
WHERE n.nspname=current_schema() AND p.prokind='f' AND p.proname=ANY(tracked_functions)
 ), encoded AS (SELECT pg_temp.tide_pricing_json_row(fields) AS row_value FROM metadata)
 SELECT encode(sha256(convert_to(string_agg(row_value,chr(10) ORDER BY row_value COLLATE "C"),'UTF8')),'hex') FROM encoded
 );
END $fn$;

DO $migration$
DECLARE applied_versions bigint[]; current_fingerprint text; before_orders bigint;
BEGIN
 IF current_schema() IS DISTINCT FROM 'tide_casa' OR current_schemas(false) IS DISTINCT FROM ARRAY['tide_casa']::name[] THEN
  RAISE EXCEPTION 'Expected only the existing tide_casa application schema';
 END IF;
 IF to_regclass('tide_casa.tide_service_orders') IS NULL OR to_regclass('tide_casa.tide_postgres_migrations') IS NULL THEN
  RAISE EXCEPTION 'The existing application billing schema and ledger are required';
 END IF;
 IF NOT pg_has_role(current_user,(SELECT relowner FROM pg_class WHERE oid='tide_casa.tide_service_orders'::regclass),'USAGE') THEN
  RAISE EXCEPTION 'Run this migration as the existing billing table owner';
 END IF;
 SELECT array_agg(version ORDER BY version) INTO applied_versions FROM tide_postgres_migrations;
 IF applied_versions IS DISTINCT FROM ARRAY[1,2]::bigint[] AND applied_versions IS DISTINCT FROM ARRAY[1,2,3]::bigint[] THEN
  RAISE EXCEPTION 'Expected the reviewed v2 pricing ledger (or already-applied v3)';
 END IF;
 IF NOT EXISTS(SELECT 1 FROM tide_postgres_migrations WHERE version=1
  AND resource_name='TideCasa.Api.PostgresMigrations.0001_baseline.sql' AND sha256='544d9f3d6f972cdbc36fb4f03ef78906f947190f1de575313b1d1aa16c53fd1a' AND manifest_sha256='719e7b03fd36453dafc511dc363a09314d6a392a29d38a9d1a2954428a95589e')
 OR NOT EXISTS(SELECT 1 FROM tide_postgres_migrations WHERE version=2
  AND resource_name='TideCasa.Api.PostgresMigrations.0002_service_pricing.sql'
  AND sha256='68c196ccc4a38e115849c92a58e3658dbb53cf74f8bb768d82f5cc716859c7f6' AND manifest_sha256='68c196ccc4a38e115849c92a58e3658dbb53cf74f8bb768d82f5cc716859c7f6') THEN
  RAISE EXCEPTION 'The baseline or v2 migration checksum does not match this release';
 END IF;
 current_fingerprint := pg_temp.tide_pricing_schema_hash();
 IF current_fingerprint IS DISTINCT FROM (SELECT schema_sha256 FROM tide_postgres_migrations ORDER BY version DESC LIMIT 1) THEN
  RAISE EXCEPTION 'Existing schema fingerprint differs: do not rewrite history to suppress drift';
 END IF;
 IF applied_versions=ARRAY[1,2,3]::bigint[] THEN
  IF NOT EXISTS(SELECT 1 FROM tide_postgres_migrations WHERE version=3
   AND resource_name='TideCasa.Api.PostgresMigrations.0003_service_pricing_upfront.sql'
   AND sha256='8823457cc99016d54d4dcfd88742dd2d897c5bfbb8ceae2ae0ca8611486db716' AND manifest_sha256='8823457cc99016d54d4dcfd88742dd2d897c5bfbb8ceae2ae0ca8611486db716') THEN
   RAISE EXCEPTION 'Version 3 is present with an unexpected migration checksum';
  END IF;
  RAISE NOTICE 'Pricing v3 is already applied and its schema fingerprint matches';
  RETURN;
 END IF;
 SELECT count(*) INTO before_orders FROM tide_service_orders;
 -- Exact SQL embedded in this release; all three old contracts stay valid.
-- Keep saved contracts unchanged; require the first $199 month on new purchases.
ALTER TABLE tide_service_orders DROP CONSTRAINT pgck_tide_service_orders_03;
ALTER TABLE tide_service_orders ADD CONSTRAINT pgck_tide_service_orders_03 CHECK (initial_cents BETWEEN 0 AND CASE WHEN monthly_cents=19900 THEN 180000 ELSE 90000 END);
ALTER TABLE tide_service_orders DROP CONSTRAINT pgck_tide_service_orders_04;
ALTER TABLE tide_service_orders ADD CONSTRAINT pgck_tide_service_orders_04 CHECK (monthly_cents IN (5000,14900,19900));
ALTER TABLE tide_service_orders DROP CONSTRAINT pgck_tide_service_orders_05;
ALTER TABLE tide_service_orders ADD CONSTRAINT pgck_tide_service_orders_05 CHECK ((monthly_cents IN (5000,19900) AND total_cents=initial_cents+monthly_cents) OR (monthly_cents=14900 AND total_cents=initial_cents));

 IF (SELECT count(*) FROM tide_service_orders)<>before_orders THEN
  RAISE EXCEPTION 'Billing record count changed unexpectedly';
 END IF;
 INSERT INTO tide_postgres_migrations(version,resource_name,sha256,manifest_sha256,schema_sha256,applied_at)
 VALUES(3,'TideCasa.Api.PostgresMigrations.0003_service_pricing_upfront.sql',
  '8823457cc99016d54d4dcfd88742dd2d897c5bfbb8ceae2ae0ca8611486db716','8823457cc99016d54d4dcfd88742dd2d897c5bfbb8ceae2ae0ca8611486db716',pg_temp.tide_pricing_schema_hash(),
  to_char(clock_timestamp() AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'+00:00');
 RAISE NOTICE 'Pricing v3 applied; saved orders/subscriptions preserved';
END $migration$;
COMMIT;

SELECT version,resource_name,sha256,schema_sha256 FROM tide_casa.tide_postgres_migrations ORDER BY version;
