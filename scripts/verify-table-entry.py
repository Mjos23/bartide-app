"""Real local API regression checks for manual and QR dine-in table assignment."""
import json
import os
from pathlib import Path
import shutil
import uuid
import restaurant_test_support as s

source = s.ROOT
s.RUN = source / '.tools/table-entry-verification' / s.datetime.now(s.timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
s.RUN.mkdir(parents=True)
s.DB = s.RUN / 'synthetic.db'
snapshot = s.RUN / 'runtime'
shutil.copytree(source / 'TideCasa.Api/bin/Debug/net10.0', snapshot / 'TideCasa.Api/bin/Debug/net10.0')
s.ROOT = snapshot
os.environ['TIDE_TEST_BUILD_ROOT'] = str(snapshot)
for key in list(os.environ):
    if key.upper().startswith(('CONNECTIONSTRINGS__','REVERSEPROXY__','WEBPUSH__','DATAPROTECTION__')): os.environ.pop(key)

def quote(label=None, token=None, tenant='bistro', fulfillment='dine-in'):
    request = {'items':[{'itemId':'dish-1','quantity':2}], 'fulfillment':fulfillment,
               'paymentMethod':'staff', 'tableLabel':label, 'tableToken':token}
    return request, s.call('/api/v1/restaurants/'+tenant+'/quote',request)

try:
    s.launch(); s.seed('bistro'); s.seed('foreign',s.BOB)
    request, result = quote('1')
    s.check('Typed table number resolves an active local table',result[0]==200 and result[1]['tableLabel']=='Table 1',result[:3])
    for label in ['Table 1',' table 1 ','TABLE 1']:
        s.check('Exact table name is trimmed and case-insensitive: '+label,quote(label)[1][0]==200)
    s.check('QR orders still resolve their table',quote(token=s.TABLE)[1][1]['tableLabel']=='Table 1')
    s.check('A matching label and QR agree',quote('1',s.TABLE)[1][0]==200)
    s.check('Mismatched QR and typed label cannot retarget an order',quote('2',s.TABLE)[1][0]==400)
    for label in ['999','Table 3','Foreign Table','x'*41,'1\n2','<script>']:
        s.check('Invalid or disabled table rejected: '+repr(label),quote(label)[1][0] in (400,404))
    s.check('Dine-in requires a table',quote()[1][0]==400)
    s.check('Whitespace is not a table',quote('  ')[1][0]==400)
    s.check('Pickup cannot carry a table',quote('1',fulfillment='pickup')[1][0]==400)
    s.check('A table name from another venue is rejected',quote('Table 1',tenant='foreign')[1][0]==404)
    order = {'requestKey':str(uuid.uuid4()),'trackingKey':uuid.uuid4().hex+uuid.uuid4().hex,'order':request,
        'quoteFingerprint':result[1]['fingerprint'],'customerName':'Table entry synthetic guest','phone':'','note':'Isolated test'}
    placed = s.call('/api/v1/restaurants/bistro/orders',order)
    s.check('Manual table entry creates a dine-in order',placed[0]==201,placed[:3])
    s.check('Receipt shows the canonical table label',placed[1]['quote']['tableLabel']=='Table 1')
    stored = json.loads(s.sql('SELECT payload_json FROM bartide_enhanced_orders WHERE id=?',(placed[1]['orderId'],))[0][0])
    s.check('Stored order carries the real table ID, not arbitrary guest text',stored['table_id']==s.TABLE_ID and stored['table_label']=='Table 1')
    replay = s.call('/api/v1/restaurants/bistro/orders',order)
    s.check('Manual order retries remain idempotent',replay[0]==200 and replay[1]['orderId']==placed[1]['orderId'])
    changed = {**order,'order':{**request,'tableLabel':'2'}}
    s.check('Reusing a key for another table conflicts',s.call('/api/v1/restaurants/bistro/orders',changed)[0]==409)
    # An old quote must not silently send a guest to another table.
    changed.update(requestKey=str(uuid.uuid4()),trackingKey=uuid.uuid4().hex+uuid.uuid4().hex)
    s.check('Changing table requires a fresh quote',s.call('/api/v1/restaurants/bistro/orders',changed)[0]==409)
    def disable(cfg): cfg['checkout']['tables'][0]['enabled']=False
    s.alter_config(disable)
    fresh={**order,'requestKey':str(uuid.uuid4()),'trackingKey':uuid.uuid4().hex+uuid.uuid4().hex}
    s.check('Disabling a table after review prevents new submission',s.call('/api/v1/restaurants/bistro/orders',fresh)[0]==404)
    s.check('QR cannot bypass a disabled table',quote(token=s.TABLE)[1][0]==404)
    s.check('Only the one valid order was stored',s.sql('SELECT COUNT(*) FROM bartide_enhanced_orders')[0][0]==1)
finally:
    for proc in s.PROCESSES:
        if proc.poll() is None: proc.terminate(); proc.wait(timeout=20)
    s.PROVIDER.shutdown(); s.PROVIDER.server_close()
    for log in s.LOGS: log.close()
    (s.RUN/'results.json').write_text(json.dumps(s.RESULTS,indent=2),encoding='utf-8')
    print('Table entry evidence: '+str(s.RUN),flush=True)
