"""Verify fresh, populated-legacy upgrade and restart behavior on the owned local PostgreSQL fixture."""
import importlib.util
from pathlib import Path
import sys

spec = importlib.util.spec_from_file_location('pricing_pg_checks', Path(__file__).with_name('verify-postgres-schema.py'))
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)
failed = False
try:
    schema = p.create_schema('pricing')
    p.run_api('pricing-fresh', schema)
    ledger = p.rows(schema, 'SELECT version,resource_name,sha256,schema_sha256 FROM tide_postgres_migrations ORDER BY version')
    p.check('Fresh startup applies immutable baseline and pricing upgrade', [r[0] for r in ledger] == [1, 2])
    p.modify(schema, "INSERT INTO bartide_customers(id,slug,email,name,menu_json,enrollment_note,created_at,updated_at) VALUES('billing-old','billing-old','old@example.invalid','Old','{}','fixture','2026-01-01','2026-01-01')")
    p.modify(schema, "INSERT INTO tide_service_orders(id,tenant_id,environment,request_json,initial_cents,monthly_cents,total_cents,created_at,updated_at) VALUES('old','billing-old','sandbox','{}',60000,5000,65000,'2026-01-01','2026-01-01')")
    p.modify(schema, "INSERT INTO tide_service_invoices(id,order_id,kind,amount_cents,status,updated_at) VALUES('in_old','old','initial',65000,'paid','2026-01-01')")
    p.modify(schema, "INSERT INTO tide_service_refunds(id,invoice_id,charge_id,amount_cents,status,updated_at) VALUES('re_old','in_old','ch_old',1000,'succeeded','2026-01-01')")
    # Restore only this synthetic schema to the actual version-1 constraints and ledger.
    p.modify(schema, '''ALTER TABLE tide_service_orders DROP CONSTRAINT pgck_tide_service_orders_04;
        ALTER TABLE tide_service_orders ADD CONSTRAINT pgck_tide_service_orders_04 CHECK (monthly_cents=5000);
        ALTER TABLE tide_service_orders DROP CONSTRAINT pgck_tide_service_orders_05;
        ALTER TABLE tide_service_orders ADD CONSTRAINT pgck_tide_service_orders_05 CHECK (total_cents=initial_cents+monthly_cents);
        DELETE FROM tide_postgres_migrations WHERE version=2;''')
    p.run_api('pricing-populated-upgrade', schema)
    p.check('Upgrade preserves legacy order totals and captured invoices/refunds',
        p.rows(schema, 'SELECT monthly_cents,total_cents FROM tide_service_orders') == [(5000, 65000)]
        and p.rows(schema, 'SELECT amount_cents FROM tide_service_invoices') == [(65000,)]
        and p.rows(schema, 'SELECT amount_cents FROM tide_service_refunds') == [(1000,)])
    p.modify(schema, "UPDATE tide_service_orders SET status='expired' WHERE id='old'")
    p.modify(schema, "INSERT INTO tide_service_orders(id,tenant_id,environment,request_json,initial_cents,monthly_cents,total_cents,created_at,updated_at) VALUES('new','billing-old','sandbox','{}',60000,14900,60000,'2026-01-01','2026-01-01')")
    p.check('Upgraded PostgreSQL accepts 600 now and 149 per month', p.rows(schema, "SELECT initial_cents,monthly_cents,total_cents FROM tide_service_orders WHERE id='new'") == [(60000,14900,60000)])
    p.expect_state('Database rejects an immediate new-plan monthly charge', '23514', lambda: p.modify(schema, "UPDATE tide_service_orders SET total_cents=74900 WHERE id='new'"))
    before = p.snapshot(schema)
    p.run_api('pricing-restart', schema)
    p.check('Restart preserves prices, relationships and immutable migration history', p.snapshot(schema) == before)
    p.modify(schema, "UPDATE tide_postgres_migrations SET sha256=repeat('f',64) WHERE version=2")
    p.run_api('pricing-checksum', schema, False, 'pricing migration checksum differs')
except Exception as error:
    failed = True
    p.RESULTS.append({'check':'Harness completion','passed':False,'error':p.redact(str(error))})
    print('FAIL ' + p.redact(str(error)), flush=True)
finally:
    for record in p.PROCESSES: p.stop(record)
    for schema in reversed(p.OWNED):
        # create_schema records only successfully created, uniquely prefixed fixtures.
        assert schema.startswith(p.PREFIX + '_')
        with p.connect() as db: db.execute(p.sql.SQL('DROP SCHEMA {} CASCADE').format(p.sql.Identifier(schema)))
    p.save()
print(str(p.RUN / 'results.json'), flush=True)
sys.exit(1 if failed else 0)
