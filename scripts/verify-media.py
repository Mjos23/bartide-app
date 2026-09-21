"""Private media API and native-form checks against isolated synthetic storage.

Copies compiled assemblies before launch, so canonical build outputs stay unlocked.
Only the identity-provider boundary is fake; SQLite, HTTP, JPEG re-encoding, local
private object storage, native cookie forms, authorization and streaming are real.
No production credentials, accounts, buckets or objects are used.
"""
import base64
import concurrent.futures
from datetime import datetime, timezone
import importlib.util
import http.client
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

import restaurant_test_support as s

if os.name == 'nt':
    # Inherit a process-local error mode in intentional failed-start probes so
    # Windows does not leave an unattended crash-report dialog waiting for input.
    import ctypes
    ctypes.windll.kernel32.SetErrorMode(0x0001 | 0x0002)

RUN = s.ROOT / '.tools/media-verification' / datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
RUN.mkdir(parents=True)
s.RUN, s.DB = RUN, RUN / 'synthetic.db'
OBJECTS = RUN / 'private-objects'
spec = importlib.util.spec_from_file_location('media_web_helpers', s.ROOT / 'scripts/verify-management-web.py')
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)
w.RUN = RUN
WEB = w.WEB
PDF = b'%PDF-1.4\n% Synthetic test fixture only\n1 0 obj <</Type /Catalog>> endobj\n%%EOF\n'
MP4 = struct.pack('>I', 24) + b'ftypisom\x00\x00\x02\x00isomiso2' + struct.pack('>I', 32) + b'mdat' + b'SYNTHETIC-MEDIA-CONTENT!!'
JPEG = base64.b64decode('/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAAIAAgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDyyiiivvT+RD//2Q==')


def request(path, body=None, token=None, method=None, headers=None, web=False, client=None):
    header = {'X-Forwarded-For': '192.0.2.88', **(headers or {})}
    if token:
        header['Authorization'] = 'Bearer ' + token
    req = urllib.request.Request((WEB if web else s.API) + path, data=body, method=method, headers=header)
    opener = client or urllib.request.build_opener(urllib.request.ProxyHandler({}), s.NoRedirect())
    try:
        response = opener.open(req, timeout=60)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        return response.status, response.read(), response.headers


def upload(token, kind='menu', body=PDF, tenant='bistro', key=None, name='Synthetic menu.pdf', content_type=None):
    kind_type = content_type or {'menu': 'application/pdf', 'photo': 'image/jpeg', 'video': 'video/mp4'}[kind]
    return request(f'/api/v1/tenants/{tenant}/media/{kind}', body, token,
                   headers={'Content-Type': kind_type, 'X-Request-Id': key or str(uuid.uuid4()), 'X-File-Name': urllib.parse.quote(name, safe='')})


def value(response):
    return json.loads(response[1])


def declared_upload(token, kind, length):
    connection = http.client.HTTPConnection('127.0.0.1', s.API_PORT, timeout=15)
    try:
        connection.putrequest('POST', '/api/v1/tenants/bistro/media/' + kind)
        for name, value in {'Authorization': 'Bearer ' + token, 'Content-Type': 'video/mp4' if kind == 'video' else 'application/pdf',
                            'X-Request-Id': str(uuid.uuid4()), 'X-File-Name': 'Synthetic', 'Content-Length': str(length)}.items():
            connection.putheader(name, value)
        connection.endheaders()
        return connection.getresponse().status
    finally:
        connection.close()


def startup_restriction(environment, local_root, label, expected):
    env = os.environ.copy()
    for name in list(env):
        if any(word in name.upper() for word in ('STRIPE', 'RESEND', 'SUPABASE', 'CLOUDFLARE', 'AUTH__', 'ORDERING__', 'STORAGE__', 'API__', 'DATAPROTECTION__', 'MEDIA__')): env.pop(name)
    probe_port = s.port()
    env.update({'ASPNETCORE_ENVIRONMENT': environment, 'ASPNETCORE_URLS': 'http://127.0.0.1:' + str(probe_port),
                'Storage__DatabasePath': str(RUN / (label + '.db')), 'Auth__Enabled': 'false',
                'Media__Provider': 'local', 'Media__AllowLocalStore': 'true', 'Media__LocalRoot': str(local_root)})
    output = RUN / (label + '.log')
    with output.open('w', encoding='utf-8') as log:
        proc = subprocess.Popen([str(s.SDK), str(RUN / 'assemblies/TideCasa.Api/TideCasa.Api.dll')], cwd=s.ROOT / 'TideCasa.Api',
                                env=env, stdout=log, stderr=subprocess.STDOUT,
                                creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        s.PROCESSES.append(proc)
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline and expected not in output.read_text(encoding='utf-8') and proc.poll() is None: time.sleep(.1)
        failed_start = expected in output.read_text(encoding='utf-8')
        listening = False
        try:
            with urllib.request.build_opener(urllib.request.ProxyHandler({})).open('http://127.0.0.1:' + str(probe_port) + '/health', timeout=1) as response:
                listening = response.status == 200
        except (OSError, urllib.error.URLError): pass
        if proc.poll() is None: proc.terminate(); proc.wait(timeout=10)
    s.check(label, failed_start and not listening)


def launch(project, address, extra, suffix=''):
    snapshot = RUN / 'assemblies' / project
    if not snapshot.exists():
        shutil.copytree(Path(os.environ.get('TIDE_TEST_BUILD_ROOT', str(s.ROOT))) / project / 'bin/Debug/net10.0', snapshot, ignore=shutil.ignore_patterns("libSkiaSharp.pdb"))
    env = os.environ.copy()
    for name in list(env):
        if any(word in name.upper() for word in ('STRIPE', 'RESEND', 'SUPABASE', 'CLOUDFLARE', 'AUTH__', 'ORDERING__', 'STORAGE__', 'API__', 'DATAPROTECTION__', 'MEDIA__')):
            env.pop(name)
    env.update({'ASPNETCORE_ENVIRONMENT': 'Development', 'ASPNETCORE_URLS': address,
                'Stripe__CheckoutEnabled': 'false', 'Stripe__InvoicesEnabled': 'false', **extra})
    log = (RUN / (project + suffix + '.log')).open('w', encoding='utf-8')
    s.LOGS.append(log)
    proc = subprocess.Popen([str(s.SDK), str(snapshot / (project + '.dll'))], cwd=s.ROOT / project,
                            env=env, stdout=log, stderr=subprocess.STDOUT,
                            creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    s.PROCESSES.append(proc)
    limit = time.monotonic() + 120
    while time.monotonic() < limit:
        if proc.poll() is not None:
            raise RuntimeError('Fixture stopped: ' + str(RUN / (project + '.log')))
        try:
            with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(address + '/health', timeout=3) as response:
                if response.status == 200:
                    return proc
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(.25)
    raise TimeoutError('Fixture launch timeout')


def seed_record(kind, tenant='bistro', status='pending', size=32, old=False, state='pending'):
    ident = str(uuid.uuid4())
    prefix = {'photo': 'menu-photos', 'menu': 'source-menus', 'video': 'course-videos'}[kind]
    key = f'{prefix}/{tenant}/{ident}' + ('.jpg' if kind == 'photo' else '.mp4' if kind == 'video' else '')
    now = '2020-01-01T00:00:00.0000000+00:00' if old else datetime.now(timezone.utc).isoformat()
    if kind == 'photo':
        s.sql('INSERT INTO bartide_photos(id,tenant_id,object_key,byte_size,width,height,status,created_at) VALUES(?,?,?,?,8,8,?,?)', (ident, tenant, key, size, status, now))
    elif kind == 'menu':
        s.sql('INSERT INTO bartide_menu_files(id,tenant_id,object_key,name,content_type,byte_size,status,created_at) VALUES(?,?,?,?,?,?,?,?)', (ident, tenant, key, 'Synthetic.pdf', 'application/pdf', size, status, now))
    else:
        s.sql('INSERT INTO fit_videos(id,tenant_id,object_key,name,byte_size,status,created_at) VALUES(?,?,?,?,?,?,?)', (ident, tenant, key, 'Synthetic.mp4', size, status, now))
    s.sql('INSERT INTO tide_media_operations(id,tenant_id,kind,request_key,request_hash,media_id,object_key,state,created_at,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?)', (str(uuid.uuid4()), tenant, kind, str(uuid.uuid4()), 'synthetic', ident, key, state, now, now))
    return ident, key


def multipart(form, body=PDF, filename='Web menu.pdf', content_type='application/pdf', remove=()):
    boundary = 'tide-fixture-' + uuid.uuid4().hex
    chunks = []
    for key, val in form['fields'].items():
        if key not in remove:
            chunks.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{key}"\r\n\r\n{val}\r\n'.encode())
    chunks.append(f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{filename}"\r\nContent-Type: {content_type}\r\n\r\n'.encode() + body + b'\r\n')
    chunks.append(f'--{boundary}--\r\n'.encode())
    return b''.join(chunks), 'multipart/form-data; boundary=' + boundary


def run():
    api_settings = {'Storage__DatabasePath': str(s.DB), 'Auth__Enabled': 'true', 'Auth__AllowLocalTestProvider': 'true',
        'Auth__SupabaseUrl': f'http://127.0.0.1:{s.PROVIDER.server_port}', 'Auth__PublishableKey': s.KEY,
        'Auth__PlatformOwnerUserId': 'supabase:' + s.PLATFORM, 'ReverseProxy__KnownClientProxy': '127.0.0.1',
        'Media__Provider': 'local', 'Media__AllowLocalStore': 'true', 'Media__LocalRoot': str(OBJECTS), 'Media__DevelopmentCleanupSeconds': '1'}
    api_process = launch('TideCasa.Api', s.API, api_settings)
    for args in [('bistro',), ('foreign', s.BOB), ('draft', s.ALICE, 'draft'), ('paused', s.ALICE, 'paused'), ('count-limit',), ('byte-limit',), ('recover',), ('put-failure',), ('db-failure',)]:
        s.seed(*args)
    tokens = {person: s.call('/api/v1/auth/signin', {'email': person + '@example.invalid', 'password': s.PASSWORD})[1]['accessToken'] for person in ['alice', 'bob', 'staff']}
    owner, other, staff = tokens['alice'], tokens['bob'], tokens['staff']
    root = '/api/v1/tenants/bistro/media'
    s.check('Anonymous media list denied', request(root)[0] == 401)
    s.check('Unrelated account media list denied', request(root, token=other)[0] == 403)
    s.check('Staff cannot manage media', request(root, token=staff)[0] == 403)
    s.check('Draft owner can prepare private files', request('/api/v1/tenants/draft/media', token=owner)[0] == 200)
    s.check('Paused owner loses media access', request('/api/v1/tenants/paused/media', token=owner)[0] == 403)
    workspace = value(request(root, token=owner))
    s.check('Local adapter is explicitly labelled and ready', workspace['storageReady'] and workspace['storageLabel'] == 'Local preview storage')
    s.check('Unrelated upload denied before storage', upload(other)[0] == 403)
    s.check('Invalid kind rejected', request(root + '/bad', PDF, owner)[0] == 400)
    s.check('Forged content type rejected', upload(owner, content_type='text/html')[0] == 415)
    s.check('Invalid operation ID rejected', upload(owner, key='not-a-guid')[0] == 400)
    s.check('Malformed PDF rejected', upload(owner, body=b'%PDF-incomplete')[0] == 400)
    s.check('Empty upload rejected', upload(owner, body=b'')[0] == 400)
    s.check('Compressed body rejected', request(root + '/menu', PDF, owner, headers={'Content-Encoding': 'gzip', 'Content-Type': 'application/pdf'})[0] == 400)
    s.check('Declared oversized video rejected before reading 50 MiB body', declared_upload(owner, 'video', 50 * 1024 * 1024 + 1) == 400)
    operation = str(uuid.uuid4())
    pdf = upload(owner, key=operation, name='../../Synthetic \"menu\".pdf')
    s.check('Owner uploads private PDF', pdf[0] == 200, pdf[:2])
    pdf_file = value(pdf)['file']; pdf_id = pdf_file['id']
    s.check('Filename is safe display metadata', all(char not in pdf_file['name'] for char in '/\\"'))
    s.check('Upload metadata never exposes an object key', 'objectKey' not in json.dumps(value(pdf)) and str(OBJECTS) not in json.dumps(value(pdf)))
    retried = upload(owner, key=operation, name='../../Synthetic \"menu\".pdf')
    s.check('Same operation and bytes return same file', retried[0] == 200 and value(retried)['file']['id'] == pdf_id)
    s.check('Operation with different payload conflicts', upload(owner, key=operation, body=PDF + b' ', name='../../Synthetic \"menu\".pdf')[0] == 409)
    path = root + '/menu/' + pdf_id
    content = request(path, token=owner)
    s.check('Authorized download returns exact PDF bytes', content[0] == 200 and content[1] == PDF)
    s.check('PDF is attachment only with restrictive headers', 'attachment' in content[2].get('Content-Disposition', '') and 'sandbox' in content[2].get('Content-Security-Policy', '') and content[2].get('X-Content-Type-Options') == 'nosniff' and 'no-store' in content[2].get('Cache-Control', ''))
    for token in [None, other, staff]:
        s.check('Private PDF GET denied for non-owner', request(path, token=token)[0] in (401, 404))
        s.check('Private PDF HEAD denied for non-owner', request(path, token=token, method='HEAD')[0] in (401, 404))
    s.check('Swapped tenant cannot read existing ID', request('/api/v1/tenants/foreign/media/menu/' + pdf_id, token=other)[0] == 404)
    marker = b'Exif\x00\x00SYNTHETIC-LOCATION-METADATA'
    tagged = JPEG[:2] + b'\xff\xe1' + struct.pack('>H', len(marker) + 2) + marker + JPEG[2:]
    photo = upload(owner, 'photo', tagged, name='Photo.jpg')
    s.check('JPEG decoder accepts valid image', photo[0] == 200, photo[:2])
    photo_id = value(photo)['file']['id']; photo_path = root + '/photo/' + photo_id
    optimized = request(photo_path, token=owner)
    s.check('Stored JPEG is re-encoded without source metadata', optimized[0] == 200 and b'SYNTHETIC-LOCATION-METADATA' not in optimized[1] and optimized[1] != tagged and optimized[1][:2] == b'\xff\xd8')
    too_wide = bytearray(JPEG); sof = too_wide.find(b'\xff\xc0'); too_wide[sof + 7:sof + 9] = struct.pack('>H', 1601)
    s.check('Oversized image dimensions rejected before decode', upload(owner, 'photo', bytes(too_wide), name='Wide.jpg')[0] == 400)
    s.check('Non-JPEG image body rejected', upload(owner, 'photo', PDF, name='Fake.jpg')[0] == 400)
    public_path = '/api/v1/media/photos/' + photo_id
    s.check('Unattached photo has no public read access', request(public_path)[0] == 404)
    editor = s.call('/api/v1/tenants/bistro/menu', token=owner)[1]
    item = editor['items'][0]
    attached = s.call('/api/v1/tenants/bistro/menu/items', {'expectedVersion': editor['version'], 'item': item, 'photoId': photo_id}, owner)
    s.check('Menu API attaches same-tenant ready photo', attached[0] == 200 and attached[1]['items'][0]['photoId'] == photo_id)
    foreign_photo = value(upload(other, 'photo', JPEG, tenant='foreign', name='Foreign.jpg'))['file']['id']
    pending_photo, _ = seed_record('photo')
    deleting_photo, _ = seed_record('photo', status='deleting', state='cleanup')
    for invalid_photo in [foreign_photo, pending_photo, deleting_photo]:
        editor = s.call('/api/v1/tenants/bistro/menu', token=owner)[1]
        rejected = s.call('/api/v1/tenants/bistro/menu/items', {'expectedVersion': editor['version'], 'item': item, 'photoId': invalid_photo}, owner)
        s.check('Menu rejects foreign or unfinished photo attachment', rejected[0] == 400)
    s.check('Ready photo attached to active menu is public', request(public_path)[0] == 200)
    s.check('Attached photo cannot be deleted', request(photo_path, token=owner, method='DELETE')[0] == 409)
    s.sql("UPDATE bartide_customers SET status='paused' WHERE id='bistro'")
    s.check('Pausing menu immediately removes public photo access', request(public_path)[0] == 404)
    s.sql("UPDATE bartide_customers SET status='active' WHERE id='bistro'")
    editor = s.call('/api/v1/tenants/bistro/menu', token=owner)[1]
    s.check('Owner clears a menu photo through API', s.call('/api/v1/tenants/bistro/menu/items', {'expectedVersion': editor['version'], 'item': item, 'photoId': ''}, owner)[0] == 200)
    s.check('Removing photo association immediately removes public access', request(public_path)[0] == 404)
    editor = s.call('/api/v1/tenants/bistro/menu', token=owner)[1]
    s.call('/api/v1/tenants/bistro/menu/items', {'expectedVersion': editor['version'], 'item': item, 'photoId': photo_id}, owner)
    raced_photo = value(upload(owner, 'photo', JPEG, name='Race.jpg'))['file']['id']
    editor = s.call('/api/v1/tenants/bistro/menu', token=owner)[1]
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        save = pool.submit(s.call, '/api/v1/tenants/bistro/menu/items', {'expectedVersion': editor['version'], 'item': item, 'photoId': raced_photo}, owner)
        delete = pool.submit(request, root + '/photo/' + raced_photo, token=owner, method='DELETE')
        save, delete = save.result(), delete.result()
    s.check('Menu attach versus photo delete race has one winner', (save[0], delete[0]) in [(200, 409), (400, 204)], (save[0], delete[0]))
    editor = s.call('/api/v1/tenants/bistro/menu', token=owner)[1]
    s.call('/api/v1/tenants/bistro/menu/items', {'expectedVersion': editor['version'], 'item': item, 'photoId': photo_id}, owner)
    video = upload(owner, 'video', MP4, name='Training.mp4')
    s.check('MP4 upload succeeds', video[0] == 200, video[:2]); video_id = value(video)['file']['id']; video_path = root + '/video/' + video_id
    s.check('Invalid MP4 container rejected', upload(owner, 'video', b'fake video', name='Fake.mp4')[0] == 400)
    for header, expected in [('bytes=0-9', MP4[:10]), ('bytes=8-', MP4[8:]), ('bytes=-7', MP4[-7:]), ('bytes=0-9999', MP4)]:
        read = request(video_path, token=owner, headers={'Range': header})
        s.check('Exact streamed video range ' + header, read[0] == 206 and read[1] == expected and int(read[2]['Content-Length']) == len(expected) and read[2]['Accept-Ranges'] == 'bytes')
    for header in ['bytes=1-2,4-5', 'bytes=-0', 'bytes=999999-', 'bytes=5-3', 'bytes=+1-3', 'items=1-3', 'bytes=9223372036854775808-']:
        read = request(video_path, token=owner, headers={'Range': header})
        s.check('Invalid range rejected ' + header, read[0] == 416 and read[2].get('Content-Range') == 'bytes */' + str(len(MP4)))
    head = request(video_path, token=owner, method='HEAD', headers={'Range': 'bytes=2-5'})
    s.check('HEAD range returns exact headers without content', head[0] == 206 and head[1] == b'' and head[2]['Content-Length'] == '4' and head[2]['Content-Range'] == f'bytes 2-5/{len(MP4)}')
    s.check('Unassigned staff cannot read video', request(video_path, token=staff)[0] == 404)
    member, course, lesson = [str(uuid.uuid4()) for _ in range(3)]
    s.sql('INSERT INTO bartide_enhanced_members(id,tenant_id,name,email,user_id,role,active,created_at) VALUES(?,?,?,?,?,?,1,?)', (member, 'bistro', 'Synthetic staff', 'staff@example.invalid', 'supabase:' + s.STAFF, 'kitchen', s.NOW))
    s.sql('INSERT INTO fit_courses(id,tenant_id,title,published,created_at,updated_at) VALUES(?,?,?,1,?,?)', (course, 'bistro', 'Synthetic course', s.NOW, s.NOW))
    lesson_request = {'courseId': course, 'title': 'Synthetic lesson', 'description': '', 'videoUrl': '', 'position': 1, 'uploadedVideoId': video_id}
    saved_lesson = s.call('/api/v1/tenants/bistro/team/lessons', lesson_request, owner)
    s.check('Training API attaches private uploaded video', saved_lesson[0] == 200 and any(row.get('uploadedVideoId') == video_id and row['videoSource'] == '/private-media/bistro/video/' + video_id for row in saved_lesson[1]['lessons']), saved_lesson[:2])
    foreign_video = value(upload(other, 'video', MP4, tenant='foreign', name='Foreign.mp4'))['file']['id']
    pending_video, _ = seed_record('video')
    deleting_video, _ = seed_record('video', status='deleting', state='cleanup')
    for invalid_video in [foreign_video, pending_video, deleting_video]:
        s.check('Training rejects foreign or unfinished uploaded video', s.call('/api/v1/tenants/bistro/team/lessons', {**lesson_request, 'uploadedVideoId': invalid_video}, owner)[0] == 400)
    s.check('Training requires exactly one video source', s.call('/api/v1/tenants/bistro/team/lessons', {**lesson_request, 'videoUrl': 'https://youtu.be/dQw4w9WgXcQ'}, owner)[0] == 400)
    s.sql('INSERT INTO tide_staff_course_assignments(tenant_id,member_id,course_id,active,version,assigned_at,updated_at) VALUES(?,?,?,1,0,?,?)', ('bistro', member, course, s.NOW, s.NOW))
    s.check('Assigned active staff watches exact published lesson video', request(video_path, token=staff)[0] == 200)
    s.check('Lesson-referenced video deletion rejected', request(video_path, token=owner, method='DELETE')[0] == 409)
    s.sql('UPDATE fit_courses SET published=0 WHERE id=?', (course,))
    s.check('Unpublishing course denies next staff range', request(video_path, token=staff, headers={'Range': 'bytes=0-7'})[0] == 404)
    s.sql('UPDATE fit_courses SET published=1 WHERE id=?', (course,))
    s.sql('UPDATE tide_staff_course_assignments SET active=0 WHERE member_id=?', (member,))
    s.check('Assignment revocation denies next HEAD', request(video_path, token=staff, method='HEAD')[0] == 404)
    s.sql('UPDATE tide_staff_course_assignments SET active=1 WHERE member_id=?', (member,))
    s.sql('UPDATE bartide_enhanced_members SET active=0 WHERE id=?', (member,))
    s.check('Member revocation denies next GET', request(video_path, token=staff)[0] == 404)
    s.sql('UPDATE bartide_enhanced_members SET active=1 WHERE id=?', (member,))
    for _ in range(9): seed_record('menu', 'count-limit')
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        race = list(pool.map(lambda _: upload(owner, tenant='count-limit'), range(2)))
    s.check('Concurrent uploads respect pending-inclusive count cap', sorted(response[0] for response in race) == [200, 409] and s.sql("SELECT COUNT(*) FROM bartide_menu_files WHERE tenant_id='count-limit'")[0][0] == 10)
    for _ in range(5): seed_record('menu', 'byte-limit', size=10 * 1024 * 1024)
    s.check('Pending bytes count toward tenant storage quota', upload(owner, tenant='byte-limit')[0] == 409)
    global_tenants = ['global-quota-' + str(index) for index in range(5)]
    for tenant in global_tenants: s.seed(tenant)
    for index in range(83): seed_record('video', global_tenants[index // 20], size=50 * 1024 * 1024)
    s.check('Application-wide quota includes all tenants and pending videos', upload(owner, tenant='draft')[0] == 409)
    for tenant in global_tenants:
        s.sql('DELETE FROM tide_media_operations WHERE tenant_id=?', (tenant,))
        s.sql('DELETE FROM fit_videos WHERE tenant_id=?', (tenant,))
    # A valid file exactly at 10 MiB is accepted; one byte over is rejected before storage.
    full_pdf = b'%PDF-1.4\n' + b' ' * (10 * 1024 * 1024 - 15) + b'%%EOF\n'
    s.check('Valid source menu at exact byte limit accepted', len(full_pdf) == 10 * 1024 * 1024 and upload(owner, tenant='draft', body=full_pdf)[0] == 200)
    s.check('One byte above source-menu limit rejected', upload(owner, tenant='draft', body=full_pdf + b' ')[0] in (400, 413))
    recovery_id, recovery_key = seed_record('menu', 'recover', old=True)
    recovery_path = OBJECTS / recovery_key; recovery_path.parent.mkdir(parents=True, exist_ok=True); recovery_path.write_bytes(b'known orphan'); Path(str(recovery_path) + '.type').write_text('application/pdf')
    unrelated_path = OBJECTS / 'unrelated-object'; unrelated_path.write_bytes(b'leave untouched')
    deadline = time.monotonic() + 8
    while time.monotonic() < deadline and s.sql('SELECT state FROM tide_media_operations WHERE media_id=?', (recovery_id,))[0][0] != 'removed': time.sleep(.1)
    s.check('Expired interrupted upload is safely cleaned', not recovery_path.exists() and s.sql('SELECT state FROM tide_media_operations WHERE media_id=?', (recovery_id,))[0][0] == 'removed' and not s.sql('SELECT id FROM bartide_menu_files WHERE id=?', (recovery_id,)))
    s.check('Cleanup never enumerates or deletes unrelated object keys', unrelated_path.read_bytes() == b'leave untouched')
    s.check('Recent pending upload reservations are preserved', s.sql("SELECT COUNT(*) FROM bartide_menu_files WHERE tenant_id='count-limit' AND status='pending'")[0][0] == 9)
    blocked = OBJECTS / 'source-menus/put-failure'; blocked.write_bytes(b'local fixture obstruction')
    failed_put = upload(owner, tenant='put-failure')
    s.check('Storage write failure is not reported as successful', failed_put[0] == 503)
    held = s.sql("SELECT p.id,p.status,o.state FROM bartide_menu_files p JOIN tide_media_operations o ON p.id=o.media_id WHERE p.tenant_id='put-failure'")
    s.check('Failed write retains accountable cleanup reservation', len(held) == 1 and held[0][1:] == ('pending', 'cleanup'))
    blocked.unlink()
    s.sql("UPDATE tide_media_operations SET updated_at='2020-01-01' WHERE tenant_id='put-failure'")
    s.sql("CREATE TRIGGER media_test_ready_failure BEFORE UPDATE OF status ON bartide_menu_files WHEN NEW.tenant_id='db-failure' AND NEW.status='ready' BEGIN SELECT RAISE(ABORT,'synthetic transition failure'); END")
    failed_db = upload(owner, tenant='db-failure')
    s.check('Failed ready transaction is not reported as successful', failed_db[0] == 503)
    s.sql('DROP TRIGGER media_test_ready_failure')
    held = s.sql("SELECT p.id,p.object_key,p.status,o.state FROM bartide_menu_files p JOIN tide_media_operations o ON p.id=o.media_id WHERE p.tenant_id='db-failure'")
    s.check('Stored object keeps durable reservation after database failure', len(held) == 1 and held[0][2:] == ('pending', 'cleanup') and (OBJECTS / held[0][1]).exists())
    s.sql("UPDATE tide_media_operations SET updated_at='2020-01-01' WHERE tenant_id='db-failure'")
    deadline = time.monotonic() + 8
    while time.monotonic() < deadline and s.sql("SELECT COUNT(*) FROM tide_media_operations WHERE tenant_id IN('put-failure','db-failure') AND state!='removed'")[0][0]: time.sleep(.1)
    s.check('Interrupted storage and database transitions recover conservatively', s.sql("SELECT COUNT(*) FROM tide_media_operations WHERE tenant_id IN('put-failure','db-failure') AND state!='removed'")[0][0] == 0 and not (OBJECTS / held[0][1]).exists())
    delete_failure = value(upload(owner, name='Delete retry.pdf'))['file']['id']
    delete_key = s.sql('SELECT object_key FROM bartide_menu_files WHERE id=?', (delete_failure,))[0][0]
    delete_object = OBJECTS / delete_key; delete_object.unlink(); delete_object.mkdir()
    delete_url = root + '/menu/' + delete_failure
    s.check('Failed storage deletion is not reported as removed', request(delete_url, token=owner, method='DELETE')[0] == 503)
    s.check('Failed deletion retains quota and denies reads', s.sql('SELECT status FROM bartide_menu_files WHERE id=?', (delete_failure,))[0][0] == 'deleting' and request(delete_url, token=owner)[0] == 404)
    delete_object.rmdir()
    s.check('Deletion safely retries after uncertain storage result', request(delete_url, token=owner, method='DELETE')[0] == 204 and not s.sql('SELECT id FROM bartide_menu_files WHERE id=?', (delete_failure,)))
    s.check('Private list has no tokens or keys', all(token not in json.dumps(value(request(root, token=owner))) for token in tokens.values()))

    # API mutation/failure cases and native browser forms are independent suites.
    # Restart only their owned API process so the first suite's deliberate bad
    # writes cannot exhaust the browser suite's per-user window. Keep SQLite,
    # object files and registered sessions: restart durability is still required.
    api_process.terminate(); api_process.wait(timeout=15)
    launch('TideCasa.Api', s.API, api_settings, '-ssr')
    s.check('Persisted owner session and private file survive API restart', request(path, token=owner)[0] == 200)
    launch('TideCasa.Blazor', WEB, {'Api__BaseUrl': s.API + '/', 'DataProtection__KeysPath': str(RUN / 'keys')})
    s.check('Anonymous file workspace requires login', w.web('/workspace/bistro/media')[0] in (302, 303, 401))
    owner_web, staff_web = w.login('alice'), w.login('staff')
    forms, page = w.forms_for(owner_web, '/workspace/bistro/media')
    s.check('Owner page has three protected multipart upload forms', len([form for form in forms if form['action'].endswith('/upload')]) == 3 and all('__RequestVerificationToken' in form['fields'] for form in forms))
    s.check('Media page does not disclose object keys or provider tokens', 'source-menus/' not in page[1] and all(token not in page[1] for token in tokens.values()))
    s.check('Staff cannot open owner file manager', w.web('/workspace/bistro/media', client=staff_web)[0] == 403)
    form = w.find_form(forms, '/menu/upload'); data, ctype = multipart(form)
    before = s.sql("SELECT COUNT(*) FROM bartide_menu_files WHERE tenant_id='bistro'")[0][0]
    bad = request(form['action'], data, web=True, client=owner_web, headers={'Origin': 'https://other.example.invalid', 'Content-Type': ctype})
    s.check('Cross-origin upload form rejected', bad[0] in (302, 303) and 'notice=invalid' in bad[2].get('Location', ''))
    invalid_data, invalid_type = multipart(form, remove=('__RequestVerificationToken',))
    bad = request(form['action'], invalid_data, web=True, client=owner_web, headers={'Origin': WEB, 'Content-Type': invalid_type})
    s.check('Upload without antiforgery token rejected', bad[0] in (302, 303) and 'notice=expired' in bad[2].get('Location', ''))
    s.check('Rejected web requests do not create files', s.sql("SELECT COUNT(*) FROM bartide_menu_files WHERE tenant_id='bistro'")[0][0] == before)
    result = request(form['action'], data, web=True, client=owner_web, headers={'Origin': WEB, 'Content-Type': ctype})
    uploaded = result[0] in (302, 303) and 'notice=uploaded' in result[2].get('Location', '')
    diagnostic = {'status': result[0], 'location': result[2].get('Location', '')}
    if not uploaded:
        # A missing synthetic object is safe to delete. This distinguishes the
        # API write limiter from an error in the native multipart/proxy flow.
        diagnostic['api_write_probe_status'] = request(root + '/menu/' + str(uuid.uuid4()), token=owner, method='DELETE')[0]
    s.check('Native multipart form uploads through Blazor', uploaded, diagnostic)
    web_file = s.sql("SELECT id FROM bartide_menu_files WHERE tenant_id='bistro' AND name='Web menu.pdf'")[0][0]
    file_url = '/private-media/bistro/menu/' + web_file
    proxied = request(file_url, web=True, client=owner_web)
    s.check('Blazor streams exact authorized private attachment', proxied[0] == 200 and proxied[1] == PDF and 'attachment' in proxied[2]['Content-Disposition'])
    s.check('Blazor proxies public approved menu photo', request('/media/' + photo_id, web=True)[0] == 200)
    video_url = '/private-media/bistro/video/' + video_id
    read = request(video_url, web=True, client=staff_web, headers={'Range': 'bytes=-9'})
    s.check('Staff video seeks through same-origin session proxy', read[0] == 206 and read[1] == MP4[-9:])
    s.sql('UPDATE tide_staff_course_assignments SET active=0 WHERE member_id=?', (member,))
    s.check('Existing browser session loses revoked video access immediately', request(video_url, web=True, client=staff_web, method='HEAD')[0] == 404)
    forms, _ = w.forms_for(owner_web, '/workspace/bistro/media')
    remove = w.find_form(forms, '/menu/' + web_file + '/delete')
    denied = w.post(owner_web, remove, remove=('__RequestVerificationToken',))
    s.check('Deletion requires valid antiforgery token', denied[0] in (302, 303) and 'notice=expired' in denied[2].get('Location', ''))
    result = w.post(owner_web, remove)
    s.check('Native delete removes private object and metadata', result[0] in (302, 303) and 'notice=removed' in result[2].get('Location', '') and not s.sql('SELECT id FROM bartide_menu_files WHERE id=?', (web_file,)))
    s.check('Deleted file is no longer readable', request(file_url, web=True, client=owner_web)[0] == 404)
    s.check('Owner direct deletion is idempotent', request(path, token=owner, method='DELETE')[0] == 204 and request(path, token=owner, method='DELETE')[0] == 204)
    s.check('Completed request cannot resurrect a deleted file', upload(owner, key=operation, name='../../Synthetic \"menu\".pdf')[0] == 409)
    key = s.sql('SELECT object_key FROM fit_videos WHERE id=?', (video_id,))[0][0]
    (OBJECTS / key).write_bytes(b'changed')
    s.check('Changed storage size never streams as valid file', request(video_path, token=owner)[0] == 503)
    s.check('Foreign key invariants remain intact', s.sql('PRAGMA foreign_key_check') == [])
    s.check('Upload spool files are removed', list((RUN / 'media-spool').glob('*')) == [])
    # Exercise the actual production limiter policy with a dedicated identity.
    # Use idempotent deletes of absent synthetic IDs, avoiding uploaded bytes or
    # persistent quota use while proving endpoint middleware applies to writes.
    rate_id = str(uuid.uuid4())
    s.USERS['limiter@example.invalid'] = {'id': rate_id, 'email': 'limiter@example.invalid',
        'email_confirmed_at': '2026-01-01T00:00:00Z', 'is_anonymous': False,
        'user_metadata': {'full_name': 'Synthetic rate-limit owner'}}
    s.seed('rate-owner', owner=rate_id)
    rate_token = s.call('/api/v1/auth/signin', {'email': 'limiter@example.invalid', 'password': s.PASSWORD})[1]['accessToken']
    rate_root = '/api/v1/tenants/rate-owner/media'
    missing = rate_root + '/menu/' + str(uuid.uuid4())
    statuses = [request(missing, token=rate_token, method='DELETE')[0] for _ in range(30)]
    s.check('Media write limiter permits thirty requests in a fresh user window', statuses == [204] * 30, statuses)
    blocked = request(missing, token=rate_token, method='DELETE')
    s.check('Thirty-first media write returns 429', blocked[0] == 429, {'status': blocked[0]})
    s.check('Changing IP cannot bypass verified-user media write limit', request(missing, token=rate_token, method='DELETE', headers={'X-Forwarded-For': '192.0.2.99'})[0] == 429)
    new_token = s.call('/api/v1/auth/signin', {'email': 'limiter@example.invalid', 'password': s.PASSWORD})[1]['accessToken']
    s.check('New session cannot bypass same-user media write limit', request(missing, token=new_token, method='DELETE')[0] == 429)
    s.check('Read budget remains available after write throttle', request(rate_root, token=rate_token)[0] == 200)
    s.check('Another owner at the same client IP keeps an independent write budget', request('/api/v1/tenants/foreign/media/menu/' + str(uuid.uuid4()), token=other, method='DELETE')[0] == 204)
    s.check('Throttled upload cannot reserve metadata or storage', upload(rate_token, tenant='rate-owner')[0] == 429 and not s.sql("SELECT id FROM tide_media_operations WHERE tenant_id='rate-owner'") and not (OBJECTS / 'source-menus/rate-owner').exists())
    startup_restriction('Production', RUN / 'forbidden-production-store', 'Production refuses local development storage', 'Local media storage is permitted only')
    startup_restriction('Development', s.ROOT / 'TideCasa.Api/wwwroot/private-test', 'Local storage refuses public static directory', 'Media storage cannot be public static content')


try:
    run()
    print(str(len(s.RESULTS)) + ' media API and SSR checks passed.', flush=True)
finally:
    for proc in s.PROCESSES:
        if proc.poll() is None:
            proc.terminate()
            try: proc.wait(timeout=15)
            except subprocess.TimeoutExpired: proc.kill(); proc.wait(timeout=10)
    s.PROVIDER.shutdown(); s.PROVIDER.server_close()
    for log in s.LOGS: log.close()
    (RUN / 'results.json').write_text(json.dumps(s.RESULTS, indent=2), encoding='utf-8')
    print('Media evidence: ' + str(RUN), flush=True)
