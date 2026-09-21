CREATE TABLE `bartide_enhanced_configs` (
	`tenant_id` text PRIMARY KEY NOT NULL,
	`settings_json` text NOT NULL,
	`version` integer DEFAULT 0 NOT NULL,
	`updated_at` text NOT NULL,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE TABLE `bartide_enhanced_limits` (
	`id` text PRIMARY KEY NOT NULL,
	`count` integer NOT NULL,
	`expires_at` integer NOT NULL
);
--> statement-breakpoint
CREATE TABLE `bartide_enhanced_members` (
	`id` text PRIMARY KEY NOT NULL,
	`tenant_id` text NOT NULL,
	`name` text NOT NULL,
	`email` text NOT NULL,
	`user_id` text,
	`role` text NOT NULL,
	`active` integer DEFAULT 1 NOT NULL,
	`created_at` text NOT NULL,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE UNIQUE INDEX `idx_enhanced_member_email` ON `bartide_enhanced_members` (`tenant_id`,`email`);--> statement-breakpoint
CREATE INDEX `idx_enhanced_member_user` ON `bartide_enhanced_members` (`user_id`);--> statement-breakpoint
CREATE TABLE `bartide_enhanced_messages` (
	`id` text PRIMARY KEY NOT NULL,
	`tenant_id` text NOT NULL,
	`author_id` text NOT NULL,
	`author` text NOT NULL,
	`body` text NOT NULL,
	`created_at` text NOT NULL,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE INDEX `idx_enhanced_message_time` ON `bartide_enhanced_messages` (`tenant_id`,`created_at`);--> statement-breakpoint
CREATE TABLE `bartide_enhanced_orders` (
	`id` text PRIMARY KEY NOT NULL,
	`tenant_id` text NOT NULL,
	`request_key` text NOT NULL,
	`request_hash` text NOT NULL,
	`tracking_hash` text NOT NULL,
	`payload_json` text NOT NULL,
	`status` text NOT NULL,
	`fulfillment` text NOT NULL,
	`driver_id` text,
	`version` integer DEFAULT 0 NOT NULL,
	`created_at` text NOT NULL,
	`updated_at` text NOT NULL,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE UNIQUE INDEX `idx_enhanced_order_request` ON `bartide_enhanced_orders` (`tenant_id`,`request_key`);--> statement-breakpoint
CREATE INDEX `idx_enhanced_order_queue` ON `bartide_enhanced_orders` (`tenant_id`,`status`,`created_at`);--> statement-breakpoint
CREATE TABLE `bartide_enhanced_shifts` (
	`id` text PRIMARY KEY NOT NULL,
	`tenant_id` text NOT NULL,
	`member_id` text NOT NULL,
	`starts_at` text NOT NULL,
	`ends_at` text NOT NULL,
	`label` text NOT NULL,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action,
	FOREIGN KEY (`member_id`) REFERENCES `bartide_enhanced_members`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE INDEX `idx_enhanced_shift_member` ON `bartide_enhanced_shifts` (`tenant_id`,`member_id`,`starts_at`);