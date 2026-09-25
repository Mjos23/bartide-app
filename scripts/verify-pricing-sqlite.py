"""In-memory migration checks preserve each saved pricing contract and all relations."""
from pathlib import Path
import sqlite3
root = Path(__file__).resolve().parents[1]
db = sqlite3.connect(':memory:')
db.execute('PRAGMA foreign_keys=ON')
for p in sorted((root/'TideCasa.Api/Migrations').glob('*.sql')):
    db.executescript(p.read_text())
for p in sorted((root/'TideCasa.Api/FeatureMigrations').glob('*.sql')):
    if p.name >= '0010':
        continue
    db.executescript(p.read_text())
db.execute("INSERT INTO bartide_customers(id,slug,email,name,menu_json,enrollment_note,created_at,updated_at) VALUES('old','old','old@example.invalid','Old','{}','fixture','2026-01-01','2026-01-01')")
db.execute("INSERT INTO tide_service_orders(id,tenant_id,environment,request_json,initial_cents,monthly_cents,total_cents,created_at,updated_at) VALUES('old','old','sandbox','{}',60000,5000,65000,'2026-01-01','2026-01-01')")
db.execute("INSERT INTO tide_service_invoices(id,order_id,kind,amount_cents,status,updated_at) VALUES('in_old','old','initial',65000,'paid','2026-01-01')")
db.execute("INSERT INTO tide_service_refunds(id,invoice_id,charge_id,amount_cents,status,updated_at) VALUES('re_old','in_old','ch_old',1000,'succeeded','2026-01-01')")
def upgrade(name):
    db.commit()
    db.execute('PRAGMA foreign_keys=OFF')
    db.executescript('BEGIN;' + (root/'TideCasa.Api/FeatureMigrations'/name).read_text())
    assert db.execute('PRAGMA foreign_key_check').fetchall() == []
    db.commit()
    db.execute('PRAGMA foreign_keys=ON')
def rejects(sql):
    try:
        db.execute(sql)
    except sqlite3.IntegrityError:
        return
    raise AssertionError('Invalid contract was accepted: ' + sql)
upgrade('0010_service_pricing.sql')
db.execute("UPDATE tide_service_orders SET status='expired' WHERE id='old'")
db.execute("INSERT INTO tide_service_orders(id,tenant_id,environment,request_json,initial_cents,monthly_cents,total_cents,created_at,updated_at) VALUES('deferred','old','sandbox','{}',60000,14900,60000,'2026-01-01','2026-01-01')")
upgrade('0011_service_pricing_upfront.sql')
assert db.execute('SELECT id,initial_cents,monthly_cents,total_cents FROM tide_service_orders ORDER BY id').fetchall() == [('deferred',60000,14900,60000),('old',60000,5000,65000)]
assert db.execute('SELECT amount_cents FROM tide_service_invoices').fetchall() == [(65000,)]
assert db.execute('SELECT amount_cents FROM tide_service_refunds').fetchall() == [(1000,)]
assert db.execute('PRAGMA foreign_key_check').fetchall() == []
rejects("UPDATE tide_service_orders SET total_cents=74900 WHERE id='deferred'")
rejects("UPDATE tide_service_orders SET initial_cents=150000,total_cents=150000 WHERE id='deferred'")
db.execute("UPDATE tide_service_orders SET status='expired' WHERE id='deferred'")
db.execute("INSERT INTO tide_service_orders(id,tenant_id,environment,request_json,initial_cents,monthly_cents,total_cents,created_at,updated_at) VALUES('new','old','sandbox','{}',150000,19900,169900,'2026-01-01','2026-01-01')")
rejects("UPDATE tide_service_orders SET total_cents=150000 WHERE id='new'")
rejects("UPDATE tide_service_orders SET monthly_cents=19899,total_cents=169899 WHERE id='new'")
db.execute("UPDATE tide_service_orders SET initial_cents=180000,total_cents=199900,app_stores=1 WHERE id='new'")
assert db.execute("SELECT initial_cents,monthly_cents,total_cents FROM tide_service_orders WHERE id='new'").fetchone() == (180000,19900,199900)
rejects("UPDATE tide_service_orders SET initial_cents=180001,total_cents=199901 WHERE id='new'")
print('PASS SQLite pricing upgrade preserves $50 and deferred $149 contracts, invoices/refunds and relationships; requires build plus the $199 first month ($1,699/$1,999).')
