from pathlib import Path
import sqlite3
root=Path(__file__).resolve().parents[1]
db=sqlite3.connect(':memory:')
db.execute('PRAGMA foreign_keys=ON')
for p in sorted((root/'TideCasa.Api/Migrations').glob('*.sql')):db.executescript(p.read_text())
for p in sorted((root/'TideCasa.Api/FeatureMigrations').glob('*.sql')):
    if p.name.startswith('0010'):continue
    db.executescript(p.read_text())
db.execute("INSERT INTO bartide_customers(id,slug,email,name,menu_json,enrollment_note,created_at,updated_at) VALUES('old','old','old@example.invalid','Old','{}','fixture','2026-01-01','2026-01-01')")
db.execute("INSERT INTO tide_service_orders(id,tenant_id,environment,request_json,initial_cents,monthly_cents,total_cents,created_at,updated_at) VALUES('old','old','sandbox','{}',60000,5000,65000,'2026-01-01','2026-01-01')")
db.execute("INSERT INTO tide_service_invoices(id,order_id,kind,amount_cents,status,updated_at) VALUES('in_old','old','initial',65000,'paid','2026-01-01')")
db.execute("INSERT INTO tide_service_refunds(id,invoice_id,charge_id,amount_cents,status,updated_at) VALUES('re_old','in_old','ch_old',1000,'succeeded','2026-01-01')")
db.commit()
db.execute('PRAGMA foreign_keys=OFF')
db.executescript('BEGIN;'+(root/'TideCasa.Api/FeatureMigrations/0010_service_pricing.sql').read_text())
assert db.execute('PRAGMA foreign_key_check').fetchall()==[]
db.commit()
db.execute('PRAGMA foreign_keys=ON')
assert db.execute('SELECT monthly_cents,total_cents FROM tide_service_orders').fetchall()==[(5000,65000)]
assert db.execute('PRAGMA foreign_key_check').fetchall()==[]
assert db.execute('SELECT count(*) FROM tide_service_refunds').fetchone()==(1,)
db.execute("UPDATE tide_service_orders SET status='expired' WHERE id='old'")
db.execute("INSERT INTO tide_service_orders(id,tenant_id,environment,request_json,initial_cents,monthly_cents,total_cents,created_at,updated_at) VALUES('new','old','sandbox','{}',60000,14900,60000,'2026-01-01','2026-01-01')")
print('PASS populated SQLite pricing upgrade preserves legacy order, invoice, refund and relationships; accepts new initial 600 / monthly 149.')
