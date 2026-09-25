-- Keep saved contracts unchanged; require the first $199 month on new purchases.
ALTER TABLE tide_service_orders DROP CONSTRAINT pgck_tide_service_orders_03;
ALTER TABLE tide_service_orders ADD CONSTRAINT pgck_tide_service_orders_03 CHECK (initial_cents BETWEEN 0 AND CASE WHEN monthly_cents=19900 THEN 180000 ELSE 90000 END);
ALTER TABLE tide_service_orders DROP CONSTRAINT pgck_tide_service_orders_04;
ALTER TABLE tide_service_orders ADD CONSTRAINT pgck_tide_service_orders_04 CHECK (monthly_cents IN (5000,14900,19900));
ALTER TABLE tide_service_orders DROP CONSTRAINT pgck_tide_service_orders_05;
ALTER TABLE tide_service_orders ADD CONSTRAINT pgck_tide_service_orders_05 CHECK ((monthly_cents IN (5000,19900) AND total_cents=initial_cents+monthly_cents) OR (monthly_cents=14900 AND total_cents=initial_cents));
