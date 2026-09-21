CREATE TABLE `bartide_menu_files` (
	`id` text PRIMARY KEY NOT NULL,
	`tenant_id` text NOT NULL,
	`object_key` text NOT NULL,
	`name` text NOT NULL,
	`content_type` text NOT NULL,
	`byte_size` integer NOT NULL,
	`status` text DEFAULT 'pending' NOT NULL,
	`created_at` text NOT NULL,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE INDEX `idx_bartide_menu_files_tenant` ON `bartide_menu_files` (`tenant_id`);--> statement-breakpoint
ALTER TABLE `bartide_customers` ADD `requested_plan` text DEFAULT 'app' NOT NULL;--> statement-breakpoint
ALTER TABLE `bartide_customers` ADD `contact_name` text DEFAULT '' NOT NULL;--> statement-breakpoint
ALTER TABLE `bartide_customers` ADD `contact_phone` text DEFAULT '' NOT NULL;--> statement-breakpoint
ALTER TABLE `bartide_customers` ADD `setup_notes` text DEFAULT '' NOT NULL;--> statement-breakpoint
ALTER TABLE `bartide_customers` ADD `submitted_at` text;