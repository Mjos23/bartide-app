CREATE TABLE `tide_leads` (
	`id` text PRIMARY KEY NOT NULL,
	`name` text NOT NULL,
	`business` text NOT NULL,
	`email` text NOT NULL,
	`phone` text NOT NULL,
	`vertical` text NOT NULL,
	`stage` text NOT NULL,
	`notes` text NOT NULL,
	`follow_up` text NOT NULL,
	`tenant_id` text,
	`version` integer DEFAULT 0 NOT NULL,
	`updated_at` text NOT NULL,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE INDEX `idx_tide_leads_stage` ON `tide_leads` (`stage`,`updated_at`);--> statement-breakpoint
CREATE TABLE `tide_posts` (
	`id` text PRIMARY KEY NOT NULL,
	`scope` text NOT NULL,
	`slug` text NOT NULL,
	`title` text NOT NULL,
	`excerpt` text NOT NULL,
	`body` text NOT NULL,
	`status` text DEFAULT 'draft' NOT NULL,
	`version` integer DEFAULT 0 NOT NULL,
	`published_at` text,
	`updated_at` text NOT NULL
);
--> statement-breakpoint
CREATE UNIQUE INDEX `idx_tide_posts_slug` ON `tide_posts` (`scope`,`slug`);--> statement-breakpoint
CREATE INDEX `idx_tide_posts_status` ON `tide_posts` (`scope`,`status`,`updated_at`);--> statement-breakpoint
CREATE TABLE `tide_document_reviews` (
	`file_id` text PRIMARY KEY NOT NULL,
	`state` text NOT NULL,
	`notes` text NOT NULL,
	`version` integer DEFAULT 0 NOT NULL,
	`updated_at` text NOT NULL,
	FOREIGN KEY (`file_id`) REFERENCES `bartide_menu_files`(`id`) ON UPDATE no action ON DELETE cascade
);
--> statement-breakpoint
CREATE TABLE `tide_support` (
	`id` text PRIMARY KEY NOT NULL,
	`tenant_id` text NOT NULL,
	`author_id` text NOT NULL,
	`author` text NOT NULL,
	`from_owner` integer NOT NULL,
	`body` text NOT NULL,
	`read_at` text,
	`created_at` text NOT NULL,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE INDEX `idx_tide_support_tenant` ON `tide_support` (`tenant_id`,`created_at`);--> statement-breakpoint
CREATE TABLE `tide_tasks` (
	`id` text PRIMARY KEY NOT NULL,
	`tenant_id` text NOT NULL,
	`title` text NOT NULL,
	`done` integer DEFAULT 0 NOT NULL,
	`due` text NOT NULL,
	`version` integer DEFAULT 0 NOT NULL,
	`updated_at` text NOT NULL,
	FOREIGN KEY (`tenant_id`) REFERENCES `bartide_customers`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE INDEX `idx_tide_tasks_tenant` ON `tide_tasks` (`tenant_id`,`updated_at`);