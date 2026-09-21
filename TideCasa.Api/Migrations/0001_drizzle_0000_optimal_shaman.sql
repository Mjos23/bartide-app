CREATE TABLE `bartide_customers` (
	`id` text PRIMARY KEY NOT NULL,
	`slug` text NOT NULL,
	`email` text NOT NULL,
	`user_id` text,
	`name` text NOT NULL,
	`menu_json` text NOT NULL,
	`version` integer DEFAULT 0 NOT NULL,
	`status` text DEFAULT 'active' NOT NULL,
	`enrollment_note` text NOT NULL,
	`created_at` text NOT NULL,
	`updated_at` text NOT NULL
);
--> statement-breakpoint
CREATE UNIQUE INDEX `idx_bartide_customer_slug` ON `bartide_customers` (`slug`);--> statement-breakpoint
CREATE UNIQUE INDEX `idx_bartide_customer_email` ON `bartide_customers` (`email`);--> statement-breakpoint
CREATE INDEX `idx_bartide_customer_user` ON `bartide_customers` (`user_id`);