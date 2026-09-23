-- Apply once as the schema owner, before the phase-two API deployment.
-- This extension belongs only to the public fictional demo.
BEGIN;
CREATE TABLE tide_demo_gulf_lantern.tide_delivery_locations (
 tenant_id TEXT NOT NULL, driver_id TEXT NOT NULL, order_id TEXT NOT NULL,
 user_id TEXT NOT NULL, assignment_at TEXT NOT NULL, session_hash TEXT NOT NULL,
 simulated BIGINT NOT NULL, sequence BIGINT NOT NULL,
 started_at TEXT NOT NULL, received_at TEXT NOT NULL, position_json TEXT NOT NULL,
 PRIMARY KEY (tenant_id,driver_id), UNIQUE(tenant_id,order_id)
);
ALTER TABLE tide_demo_gulf_lantern.tide_delivery_locations ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON tide_demo_gulf_lantern.tide_delivery_locations FROM PUBLIC, anon, authenticated;
CREATE POLICY fictional_demo_location ON tide_demo_gulf_lantern.tide_delivery_locations TO tide_demo_api USING (tenant_id='gulf-lantern' AND simulated=1) WITH CHECK (tenant_id='gulf-lantern' AND simulated=1);
GRANT SELECT,INSERT,UPDATE,DELETE ON tide_demo_gulf_lantern.tide_delivery_locations TO tide_demo_api;
COMMIT;
