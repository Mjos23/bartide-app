"""Copy only the fictional local bar into an isolated, private deployment seed.

This never connects to a provider. Public media assets contain only the 19 sample
photos/video; the database remains under .tools and never enters a source ZIP.
"""
from pathlib import Path
import hashlib, json, shutil, sqlite3

ROOT = Path(__file__).resolve().parents[1]
RUN = ROOT / '.tools/public-demo'
SOURCE = ROOT / '.tools/gulf-lantern-preview/gulf-lantern.db'
fixture = json.loads((ROOT / 'fixtures/gulf-lantern.json').read_text())
RUN.mkdir(exist_ok=True)
target = RUN / 'seed.db'
assert not target.exists(), 'Seed already exists; preserve the reviewed deployment snapshot.'
with sqlite3.connect(SOURCE.as_uri() + '?mode=ro', uri=True) as source, sqlite3.connect(target) as db:
    assert source.execute('SELECT id,slug FROM bartide_customers').fetchall() == [('gulf-lantern', 'gulf-lantern')]
    ids = {p['id'] for p in fixture['people']}
    assert {r[0] for r in source.execute('SELECT provider_user_id FROM bartide_auth_identities')} <= ids
    source.backup(db)
    names = {r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    # Session hashes, rate counters and local test auth state are not useful seed data.
    cleared = [t for t in names if 'rate_limit' in t or t in ('bartide_auth_sessions', 'bartide_auth_legacy_claims', 'bartide_enhanced_limits', 'bartide_auth_limits')]
    for name in cleared:
        assert name.replace('_', '').isalnum()
        db.execute('DELETE FROM "' + name + '"')
    files = db.execute("SELECT object_key,byte_size,'image/jpeg' FROM bartide_photos WHERE status='ready' UNION ALL SELECT object_key,byte_size,'video/mp4' FROM fit_videos WHERE status='ready'").fetchall()
    assert len(files) == 19, 'Expected only 18 photos and the sample training video.'
    media = []
    for key, size, content_type in files:
        assert key.split('/')[0] in ('menu-photos', 'course-videos') and key.split('/')[1] == 'gulf-lantern' and '..' not in key
        source_file = ROOT / '.tools/gulf-lantern-preview/private-objects' / key
        assert source_file.stat().st_size == size
        destination = ROOT / 'TideCasa.Api/DemoMedia' / key
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source_file, destination)
        media.append({'key': key, 'bytes': size, 'contentType': content_type, 'sha256': hashlib.sha256(destination.read_bytes()).hexdigest()})
    counts = {t: db.execute('SELECT count(*) FROM "' + t + '"').fetchone()[0] for t in sorted(names) if t.replace('_', '').isalnum()}
    db.commit()
    db.execute('PRAGMA wal_checkpoint(TRUNCATE)')
    db.execute('PRAGMA journal_mode=DELETE')
db.close()
source.close()
report = {'fictional': True, 'tenant': 'gulf-lantern', 'media': media, 'counts': counts, 'cleared': cleared,
          'databaseSha256': hashlib.sha256(target.read_bytes()).hexdigest()}
(RUN / 'seed-manifest.json').write_text(json.dumps(report, indent=2))
print(json.dumps({'seed': str(target), 'tenant': 'gulf-lantern', 'mediaFiles': len(media), 'mediaBytes': sum(f['bytes'] for f in media)}))
