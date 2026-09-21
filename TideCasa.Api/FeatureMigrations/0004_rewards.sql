CREATE TABLE tide_reward_rules (
 id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL REFERENCES bartide_customers(id),
 current_version_id TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 0,
 created_at TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE INDEX idx_reward_rules_tenant ON tide_reward_rules(tenant_id);
CREATE TABLE tide_reward_rule_versions (
 id TEXT PRIMARY KEY, rule_id TEXT NOT NULL REFERENCES tide_reward_rules(id),
 version INTEGER NOT NULL, title TEXT NOT NULL, reward TEXT NOT NULL,
 kind TEXT NOT NULL CHECK(kind IN('punch-card','monthly-visits','points')),
 threshold INTEGER NOT NULL CHECK(threshold>0), item_id TEXT, timezone_id TEXT NOT NULL,
 active INTEGER NOT NULL CHECK(active IN(0,1)), created_at TEXT NOT NULL,
 UNIQUE(rule_id,version)
);
CREATE TABLE tide_reward_qualifications (
 id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL REFERENCES bartide_customers(id),
 rule_id TEXT NOT NULL REFERENCES tide_reward_rules(id),
 version_id TEXT NOT NULL REFERENCES tide_reward_rule_versions(id),
 member_id TEXT NOT NULL REFERENCES tide_loyalty_members(id),
 source_key TEXT NOT NULL, source_kind TEXT NOT NULL, source_reference TEXT NOT NULL,
 units INTEGER NOT NULL CHECK(units>0), item_id TEXT, local_day TEXT, period TEXT NOT NULL,
 state TEXT NOT NULL CHECK(state IN('eligible','void')), note TEXT NOT NULL,
 actor TEXT NOT NULL, occurred_at TEXT NOT NULL, void_reason TEXT,
 UNIQUE(tenant_id,rule_id,source_key)
);
CREATE UNIQUE INDEX idx_reward_visit_day ON tide_reward_qualifications(rule_id,member_id,local_day) WHERE local_day IS NOT NULL;
CREATE INDEX idx_reward_qualification_member ON tide_reward_qualifications(tenant_id,member_id,version_id);
CREATE TABLE tide_reward_issued (
 id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL REFERENCES bartide_customers(id),
 member_id TEXT NOT NULL REFERENCES tide_loyalty_members(id),
 version_id TEXT NOT NULL REFERENCES tide_reward_rule_versions(id),
 period TEXT NOT NULL, ordinal INTEGER NOT NULL CHECK(ordinal>0),
 state TEXT NOT NULL CHECK(state IN('available','requested','fulfilled','void','cancelled')),
 points_cost INTEGER NOT NULL DEFAULT 0 CHECK(points_cost>=0),
 earned_at TEXT NOT NULL, updated_at TEXT NOT NULL, resolution_note TEXT NOT NULL DEFAULT '',
 UNIQUE(member_id,version_id,period,ordinal)
);
CREATE INDEX idx_reward_issued_tenant ON tide_reward_issued(tenant_id,member_id,state);
CREATE TABLE tide_reward_commands (
 tenant_id TEXT NOT NULL REFERENCES bartide_customers(id), actor TEXT NOT NULL,
 request_id TEXT NOT NULL, request_hash TEXT NOT NULL, result_id TEXT NOT NULL,
 message TEXT NOT NULL, created_at TEXT NOT NULL,
 PRIMARY KEY(tenant_id,actor,request_id)
);
CREATE TABLE tide_employee_reward_ledger (
 id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL REFERENCES bartide_customers(id),
 member_id TEXT NOT NULL REFERENCES bartide_enhanced_members(id),
 source_reference TEXT NOT NULL, delta INTEGER NOT NULL CHECK(delta<>0), reason TEXT NOT NULL,
 actor TEXT NOT NULL, created_at TEXT NOT NULL,
 UNIQUE(tenant_id,source_reference)
);
CREATE INDEX idx_employee_reward_member ON tide_employee_reward_ledger(tenant_id,member_id);
