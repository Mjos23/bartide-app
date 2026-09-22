"""Freeze a secret-free App Platform source candidate; this does not deploy it."""
import hashlib
import json
import re
import zipfile
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FOLDERS = (
    'TideCasa.Api', 'TideCasa.Blazor', 'TideCasa.Contracts', 'SharedHosting',
    'TideCasa.Domain', 'TideCasa.Domain.Checks', 'TideCasa.MigrationTool',
    'TideCasa.Persistence.Checks', 'deploy/app-platform',
)
EXCLUDED_DIRS = {'bin', 'obj', 'app_data', '.git', '.tools', 'secrets', 'usersecrets', 'properties', 'simulation', '__pycache__'}
EXCLUDED_NAMES = {'restaurantshowcase.razor', 'restaurantshowcasegallery.razor', 'restaurant-showcase.css'}
ALLOWED_SUFFIXES = {'.cs', '.csproj', '.razor', '.css', '.js', '.json', '.sql', '.svg', '.png', '.jpg', '.jpeg', '.webp', '.ico', '.woff', '.woff2', '.ttf', '.webmanifest', '.pdf', '.html', '.mp4', '.md', '.py', '.yaml'}
TEXT_SUFFIXES = {'.cs', '.csproj', '.razor', '.css', '.js', '.json', '.sql', '.svg', '.webmanifest', '.html', '.md', '.py', '.yaml', '.slnx', '.crt'}
PUBLIC_CERTIFICATES = {'deploy/app-platform/supabase-prod-ca-2021.crt': '700723581420dd1ac98fd7e9ac529f0ef210eadcaf87fc868a3ad7d114c2f3b7'}
SECRET = re.compile(rb'(?:[sr]k_(?:live|test)_[A-Za-z0-9]{16,}|AKIA[A-Z0-9]{16}|-----BEGIN (?:RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----)')

def selected(path):
    relative = path.relative_to(ROOT)
    if any(part.lower() in EXCLUDED_DIRS for part in relative.parts):
        return False
    name = path.name.lower()
    if name in EXCLUDED_NAMES or name.startswith('appsettings.') and name != 'appsettings.json':
        return False
    if path.is_symlink():
        raise RuntimeError('Source candidate contains a symlink: ' + relative.as_posix())
    if path.suffix.lower() not in ALLOWED_SUFFIXES and not path.name.startswith('Dockerfile.') and relative.as_posix() not in PUBLIC_CERTIFICATES:
        raise RuntimeError('Unreviewed source type: ' + relative.as_posix())
    return True

def main():
    stamp = datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S')
    target = ROOT / '.tools/release-source' / stamp
    target.mkdir(parents=True, exist_ok=False)
    inputs = [p for folder in FOLDERS for p in (ROOT / folder).rglob('*') if p.is_file() and selected(p)]
    inputs += [ROOT / name for name in ('.dockerignore', 'global.json', 'TideCasa.slnx', 'docs/postgresql-schema.md', 'docs/GULF-LANTERN-HOSTED-DEMO.md', 'fixtures/gulf-lantern.json')]
    inputs += [p for p in (ROOT / 'scripts').glob('*.py') if not p.name.startswith(('start-local', 'prepare-ordering', 'package-tested'))]
    manifest = {}
    for path in sorted(set(inputs)):
        relative = path.relative_to(ROOT).as_posix()
        data = path.read_bytes()
        if relative in PUBLIC_CERTIFICATES:
            assert hashlib.sha256(data).hexdigest() == PUBLIC_CERTIFICATES[relative], 'Public CA certificate changed; review its provider provenance'
        if path.suffix in TEXT_SUFFIXES or path.name.startswith('Dockerfile.'):
            if SECRET.search(data):
                raise RuntimeError('Possible credential in source candidate: ' + relative)
            data.decode('utf-8-sig', errors='strict')
        if path.name == 'appsettings.json':
            config = json.loads(data)
            assert 'ConnectionStrings' not in config
            auth = config.get('Auth', {})
            assert not auth.get('Enabled') and not auth.get('PublishableKey') and not auth.get('PlatformOwnerUserId')
        destination = target / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(data)
        manifest[relative] = {'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest(),
                              'gitBlobSha': hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()}
    readme = '''# BarTide application source

ASP.NET Core API and Blazor Web, with PostgreSQL durable storage for DigitalOcean App Platform.
This is a source candidate, not a claim of a completed production launch.

Build with the .NET SDK version in global.json. App Platform Dockerfiles are under deploy/app-platform.
The deployment template contains placeholders and keeps payment creation disabled until provider verification.
Configure private runtime settings in the hosting provider; never commit credentials or databases.

The migration tool defaults to read-only planning and requires explicit --apply for transactional transfer.
Preserve a consistent source backup and verify the imported values before changing production traffic.
See docs/postgresql-schema.md and deploy/app-platform/README.md for persistence/deployment details.

The canceled new restaurant simulations, local data, prospect records, private keys and credentials are excluded.
Existing application demonstrations and public assets are retained.
'''.encode()
    (target / 'README.md').write_bytes(readme)
    manifest['README.md'] = {'bytes': len(readme), 'sha256': hashlib.sha256(readme).hexdigest(),
                             'gitBlobSha': hashlib.sha1(b'blob ' + str(len(readme)).encode() + b'\0' + readme).hexdigest()}
    ignore = b'''**/bin/
**/obj/
.tools/
exports/
**/App_Data/
**/__pycache__/
**/.env
**/.env.*
**/*.db
**/*.db-*
**/*.sqlite*
**/*.pfx
**/*.p12
**/*.pem
**/*.key
**/secrets.json
**/credentials*.json
**/appsettings.Development.json
**/appsettings.Production.json
**/launchSettings.json
**/*.user
'''
    (target / '.gitignore').write_bytes(ignore)
    manifest['.gitignore'] = {'bytes': len(ignore), 'sha256': hashlib.sha256(ignore).hexdigest(),
                             'gitBlobSha': hashlib.sha1(b'blob ' + str(len(ignore)).encode() + b'\0' + ignore).hexdigest()}
    (target / 'source-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    archive = ROOT / 'exports' / ('TideCasa-AppPlatform-source-' + stamp + '.zip')
    archive.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(archive, 'x', zipfile.ZIP_DEFLATED, compresslevel=5) as output:
        for path in sorted(target.rglob('*')):
            if path.is_file(): output.write(path, path.relative_to(target).as_posix())
    with zipfile.ZipFile(archive) as output:
        assert output.testzip() is None
        assert len(output.namelist()) == len(manifest) + 1
        for name, entry in manifest.items():
            assert hashlib.sha256(output.read(name)).hexdigest() == entry['sha256']
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_suffix('.sha256').write_text(digest + '  ' + archive.name + '\n')
    result = {'source': str(target), 'archive': str(archive), 'files': len(manifest), 'bytes': archive.stat().st_size,
              'sha256': digest, 'verifiedArchive': True, 'deployed': False}
    (ROOT / '.tools/release-source/latest.json').write_text(json.dumps(result, indent=2))
    print(json.dumps(result))

if __name__ == '__main__': main()
