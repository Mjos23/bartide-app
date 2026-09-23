-- Review and apply as the schema owner BEFORE deploying the marketing API.
-- psql -v app_schema=tide_YOUR_MARKETING_SCHEMA -v app_role=tide_YOUR_API_ROLE -f this-file.sql
-- Do not apply to the separate fictional demo. This file does not send email.
\set ON_ERROR_STOP on
SELECT (:'app_schema' ~ '^tide_[a-z0-9_]{1,50}$'
    AND :'app_role' ~ '^tide_[a-z0-9_]{1,50}$'
    AND :'app_schema' <> 'tide_demo_gulf_lantern'
    AND :'app_role' <> 'tide_demo_api') AS safe_target \gset
\if :safe_target
BEGIN;
SET LOCAL search_path TO :"app_schema";
SELECT pg_advisory_xact_lock(hashtext(current_database()),hashtext(current_schema()));
CREATE TABLE tide_email_campaigns (
    id TEXT PRIMARY KEY, name TEXT NOT NULL, is_test BIGINT NOT NULL,
    link_token TEXT NOT NULL UNIQUE, created_at TEXT NOT NULL
);
CREATE TABLE tide_email_visits (
    id TEXT PRIMARY KEY, campaign_id TEXT NOT NULL REFERENCES tide_email_campaigns(id),
    created_at TEXT NOT NULL, expires_at TEXT NOT NULL
);
CREATE INDEX tide_email_visits_campaign ON tide_email_visits(campaign_id,created_at);
CREATE TABLE tide_email_clicks (
    visit_id TEXT NOT NULL REFERENCES tide_email_visits(id) ON DELETE CASCADE,
    sequence BIGINT NOT NULL, path TEXT NOT NULL, created_at TEXT NOT NULL,
    PRIMARY KEY(visit_id,sequence)
);
ALTER TABLE tide_email_campaigns ENABLE ROW LEVEL SECURITY;
ALTER TABLE tide_email_visits ENABLE ROW LEVEL SECURITY;
ALTER TABLE tide_email_clicks ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON tide_email_campaigns,tide_email_visits,tide_email_clicks FROM PUBLIC,anon,authenticated;
CREATE POLICY email_campaign_api ON tide_email_campaigns TO :"app_role" USING (true) WITH CHECK (true);
CREATE POLICY email_visit_api ON tide_email_visits TO :"app_role" USING (true) WITH CHECK (true);
CREATE POLICY email_click_api ON tide_email_clicks TO :"app_role" USING (true) WITH CHECK (true);
GRANT SELECT,INSERT ON tide_email_campaigns TO :"app_role";
GRANT SELECT,INSERT,DELETE ON tide_email_visits,tide_email_clicks TO :"app_role";
COMMIT;
\else
\echo 'Refusing invalid schema/role or fictional demo target.'
\quit 1
\endif
