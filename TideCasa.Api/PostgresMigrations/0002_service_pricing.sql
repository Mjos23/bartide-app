-- Historical orders retain their original totals and monthly amount.
ALTER TABLE tide_service_orders DROP CONSTRAINT pgck_tide_service_orders_04;
ALTER TABLE tide_service_orders ADD CONSTRAINT pgck_tide_service_orders_04 CHECK (monthly_cents IN (5000,14900));
ALTER TABLE tide_service_orders DROP CONSTRAINT pgck_tide_service_orders_05;
ALTER TABLE tide_service_orders ADD CONSTRAINT pgck_tide_service_orders_05 CHECK ((monthly_cents=5000 AND total_cents=initial_cents+monthly_cents) OR (monthly_cents=14900 AND total_cents=initial_cents));
