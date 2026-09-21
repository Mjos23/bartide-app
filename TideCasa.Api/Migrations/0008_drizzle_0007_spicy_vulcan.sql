CREATE TABLE `tide_loyalty_benefits` (
	`id` text PRIMARY KEY NOT NULL,
	`tenant_id` text NOT NULL,
	`kind` text NOT NULL,
	`title` text NOT NULL,
	`terms` text NOT NULL,
	`cost` integer DEFAULT 0 NOT NULL,
	`audience` text DEFAULT 'all' NOT NULL,
	`target` text DEFAULT '' NOT NULL,
	`starts` text DEFAULT '' NOT NULL,
	`ends` text DEFAULT '' NOT NULL,
	`active` integer DEFAULT 0 NOT NULL,
	`version` integer DEFAULT 0 NOT NULL,
	`updated_at` text NOT NULL,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE INDEX `idx_loyalty_benefit_tenant` ON `tide_loyalty_benefits` (`tenant_id`,`kind`,`active`);--> statement-breakpoint
CREATE TABLE `tide_loyalty_ledger` (
	`id` text PRIMARY KEY NOT NULL,
	`tenant_id` text NOT NULL,
	`member_id` text NOT NULL,
	`event_key` text NOT NULL,
	`delta` integer NOT NULL,
	`kind` text NOT NULL,
	`reason` text NOT NULL,
	`benefit_id` text,
	`state` text DEFAULT 'recorded' NOT NULL,
	`actor` text NOT NULL,
	`created_at` text NOT NULL,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action,
	FOREIGN KEY (`member_id`) REFERENCES `tide_loyalty_members`(`id`) ON UPDATE no action ON DELETE no action,
	FOREIGN KEY (`benefit_id`) REFERENCES `tide_loyalty_benefits`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE UNIQUE INDEX `idx_loyalty_ledger_event` ON `tide_loyalty_ledger` (`member_id`,`event_key`);--> statement-breakpoint
CREATE INDEX `idx_loyalty_ledger_tenant` ON `tide_loyalty_ledger` (`tenant_id`,`created_at`);--> statement-breakpoint
CREATE INDEX `idx_loyalty_ledger_member` ON `tide_loyalty_ledger` (`member_id`,`created_at`);--> statement-breakpoint
CREATE TABLE `tide_loyalty_members` (
	`id` text PRIMARY KEY NOT NULL,
	`tenant_id` text NOT NULL,
	`user_id` text NOT NULL,
	`email` text NOT NULL,
	`name` text NOT NULL,
	`birthday` text DEFAULT '' NOT NULL,
	`interests_json` text DEFAULT '[]' NOT NULL,
	`personalized` integer DEFAULT 0 NOT NULL,
	`version` integer DEFAULT 0 NOT NULL,
	`joined_at` text NOT NULL,
	`updated_at` text NOT NULL,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE UNIQUE INDEX `idx_loyalty_member_user` ON `tide_loyalty_members` (`tenant_id`,`user_id`);--> statement-breakpoint
CREATE INDEX `idx_loyalty_member_identity` ON `tide_loyalty_members` (`user_id`);--> statement-breakpoint
CREATE TABLE `tide_loyalty_programs` (
	`tenant_id` text PRIMARY KEY NOT NULL,
	`settings_json` text NOT NULL,
	`version` integer DEFAULT 0 NOT NULL,
	`updated_at` text NOT NULL,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action
);
