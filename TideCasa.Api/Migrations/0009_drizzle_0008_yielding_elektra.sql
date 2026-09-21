CREATE TABLE `bartide_connections` (
	`tenant_id` text PRIMARY KEY NOT NULL,
	`social_json` text DEFAULT '{}' NOT NULL,
	`version` integer DEFAULT 0 NOT NULL,
	`updated_at` text NOT NULL,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE TABLE `bartide_contact_details` (
	`member_id` text PRIMARY KEY NOT NULL,
	`tenant_id` text NOT NULL,
	`phone` text DEFAULT '' NOT NULL,
	`sms_status` text DEFAULT 'off' NOT NULL,
	`code_hash` text,
	`code_expires` text,
	`consent_at` text,
	`consent_text` text DEFAULT '' NOT NULL,
	`tags_json` text DEFAULT '[]' NOT NULL,
	`city` text DEFAULT '' NOT NULL,
	`notes` text DEFAULT '' NOT NULL,
	`version` integer DEFAULT 0 NOT NULL,
	`updated_at` text NOT NULL,
	FOREIGN KEY (`member_id`) REFERENCES `tide_loyalty_members`(`id`) ON UPDATE no action ON DELETE no action,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE INDEX `idx_bartide_contact_phone` ON `bartide_contact_details` (`tenant_id`,`phone`);--> statement-breakpoint
CREATE TABLE `bartide_growth_events` (
	`id` text PRIMARY KEY NOT NULL,
	`tenant_id` text NOT NULL,
	`member_id` text,
	`kind` text NOT NULL,
	`actor` text NOT NULL,
	`summary` text NOT NULL,
	`created_at` text NOT NULL,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action,
	FOREIGN KEY (`member_id`) REFERENCES `tide_loyalty_members`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE INDEX `idx_bartide_growth_event_tenant` ON `bartide_growth_events` (`tenant_id`,`created_at`);--> statement-breakpoint
CREATE TABLE `bartide_member_passes` (
	`member_id` text PRIMARY KEY NOT NULL,
	`tenant_id` text NOT NULL,
	`token_hash` text NOT NULL,
	`state` text NOT NULL,
	`version` integer DEFAULT 0 NOT NULL,
	`issued_at` text NOT NULL,
	`expires_at` text NOT NULL,
	FOREIGN KEY (`member_id`) REFERENCES `tide_loyalty_members`(`id`) ON UPDATE no action ON DELETE no action,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE UNIQUE INDEX `idx_bartide_pass_hash` ON `bartide_member_passes` (`token_hash`);--> statement-breakpoint
CREATE TABLE `bartide_sms_messages` (
	`id` text PRIMARY KEY NOT NULL,
	`tenant_id` text NOT NULL,
	`member_id` text NOT NULL,
	`phone` text NOT NULL,
	`body` text NOT NULL,
	`direction` text DEFAULT 'outbound' NOT NULL,
	`state` text DEFAULT 'draft' NOT NULL,
	`provider_sid` text,
	`error` text DEFAULT '' NOT NULL,
	`created_at` text NOT NULL,
	`updated_at` text NOT NULL,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action,
	FOREIGN KEY (`member_id`) REFERENCES `tide_loyalty_members`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE UNIQUE INDEX `idx_bartide_sms_provider` ON `bartide_sms_messages` (`provider_sid`);--> statement-breakpoint
CREATE INDEX `idx_bartide_sms_tenant` ON `bartide_sms_messages` (`tenant_id`,`created_at`);--> statement-breakpoint
CREATE TABLE `bartide_campaigns` (
	`id` text PRIMARY KEY NOT NULL,
	`tenant_id` text NOT NULL,
	`name` text NOT NULL,
	`channel` text NOT NULL,
	`caption` text NOT NULL,
	`active` integer DEFAULT 1 NOT NULL,
	`clicks` integer DEFAULT 0 NOT NULL,
	`version` integer DEFAULT 0 NOT NULL,
	`created_at` text NOT NULL,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE INDEX `idx_bartide_campaign_tenant` ON `bartide_campaigns` (`tenant_id`);--> statement-breakpoint
ALTER TABLE `tide_loyalty_members` ADD `source_campaign` text;--> statement-breakpoint
ALTER TABLE `tide_leads` ADD `city` text DEFAULT '' NOT NULL;--> statement-breakpoint
ALTER TABLE `tide_leads` ADD `source` text DEFAULT 'manual' NOT NULL;--> statement-breakpoint
ALTER TABLE `tide_leads` ADD `submitted_by` text;