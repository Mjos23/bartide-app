-- The imported lead table is retained. New fields do not change old stages or attribution.
ALTER TABLE tide_leads ADD COLUMN next_action TEXT NOT NULL DEFAULT '';
ALTER TABLE tide_leads ADD COLUMN assignee TEXT NOT NULL DEFAULT '';
ALTER TABLE tide_leads ADD COLUMN referral_code TEXT NOT NULL DEFAULT '';
ALTER TABLE tide_leads ADD COLUMN created_at TEXT NOT NULL DEFAULT '';
ALTER TABLE tide_leads ADD COLUMN identity_hash TEXT NOT NULL DEFAULT '';
UPDATE tide_leads SET created_at=updated_at WHERE created_at='';
CREATE INDEX tide_sales_identity ON tide_leads(identity_hash);
CREATE INDEX tide_sales_follow_up ON tide_leads(follow_up,stage);
CREATE TABLE tide_sales_demo_links (
 request_id TEXT PRIMARY KEY REFERENCES demo_requests(id),
 lead_id TEXT NOT NULL REFERENCES tide_leads(id),
 created_at TEXT NOT NULL
);
CREATE INDEX tide_sales_demo_lead ON tide_sales_demo_links(lead_id,created_at);
CREATE TABLE tide_sales_history (
 id TEXT PRIMARY KEY,
 lead_id TEXT NOT NULL REFERENCES tide_leads(id),
 kind TEXT NOT NULL CHECK(kind IN ('created','updated','demo-linked')),
 summary TEXT NOT NULL,
 private_note TEXT NOT NULL DEFAULT '',
 actor TEXT NOT NULL,
 created_at TEXT NOT NULL
);
CREATE INDEX tide_sales_history_lead ON tide_sales_history(lead_id,created_at,id);
CREATE TABLE tide_sales_operations (
 actor TEXT NOT NULL,
 request_key TEXT NOT NULL,
 operation TEXT NOT NULL,
 payload_hash TEXT NOT NULL,
 lead_id TEXT NOT NULL REFERENCES tide_leads(id),
 created_at TEXT NOT NULL,
 PRIMARY KEY(actor,request_key)
);
