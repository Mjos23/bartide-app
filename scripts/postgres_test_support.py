"""Explicit PostgreSQL storage/process adapter for the existing synthetic suites.

No application implementation is patched. Only fixture SQL and fixture child-process
configuration are adapted. Every adapter owns one freshly created loopback schema.
"""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import threading
import time
from types import ModuleType, SimpleNamespace
from urllib.parse import urlsplit
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/postgres-python'))
import psycopg
from psycopg import sql as pg_sql


def qmark_parameters(statement):
    """Change only unquoted qmark parameter markers; SQL semantics stay explicit."""
    result, quote, index = [], None, 0
    while index < len(statement):
        char = statement[index]
        if char == '%':
            result.append('%%')
        elif quote:
            result.append(char)
            if char == quote:
                if index + 1 < len(statement) and statement[index + 1] == quote:
                    result.append(quote)
                    index += 1
                else:
                    quote = None
        elif char in ("'", '"'):
            quote = char
            result.append(char)
        elif char == '?':
            result.append('%s')
        else:
            result.append(char)
        index += 1
    if quote:
        raise ValueError('Unterminated fixture SQL literal')
    return ''.join(result)


class FixtureCursor:
    def __init__(self, cursor=None, rows=None):
        self.cursor, self.rows = cursor, rows

    def fetchall(self):
        if self.rows is not None:
            return self.rows
        return self.cursor.fetchall() if self.cursor.description else []

    def fetchone(self):
        rows = self.fetchall()
        return rows[0] if rows else None

    @property
    def rowcount(self):
        return len(self.rows) if self.rows is not None else self.cursor.rowcount


class FixtureConnection:
    def __init__(self, adapter):
        self.adapter = adapter
        self.connection = adapter.checkout_fixture_connection()
        # Fixtures join the same writer contract as API workers; seed writes cannot
        # bypass an application read/check/write transaction.
        try:
            self.connection.execute('SELECT pg_advisory_xact_lock(hashtext(current_database()),hashtext(current_schema()))')
        except BaseException:
            self.connection.rollback()
            self.adapter.return_fixture_connection(self.connection)
            raise

    def __enter__(self):
        return self

    def __exit__(self, kind, value, traceback):
        try:
            self.connection.rollback() if kind else self.connection.commit()
        except BaseException:
            self.connection.rollback()
            raise
        finally:
            self.adapter.return_fixture_connection(self.connection)

    def execute(self, statement, values=()):
        rollback_triggers = {
            "CREATE TRIGGER fail_sales_demo_notification BEFORE INSERT ON tide_demo_notifications BEGIN SELECT RAISE(ABORT,'synthetic rollback'); END": ('fail_sales_demo_notification', 'tide_demo_notifications', 'INSERT', None, 'synthetic rollback'),
            "CREATE TRIGGER synthetic_outbox_failure BEFORE INSERT ON tide_demo_notifications BEGIN SELECT RAISE(ABORT,'synthetic outbox failure'); END": ('synthetic_outbox_failure', 'tide_demo_notifications', 'INSERT', None, 'synthetic outbox failure'),
            "CREATE TRIGGER synthetic_review_failure BEFORE INSERT ON tide_referral_reviews BEGIN SELECT RAISE(ABORT,'synthetic'); END": ('synthetic_review_failure', 'tide_referral_reviews', 'INSERT', None, 'synthetic'),
            "CREATE TRIGGER synthetic_push_failure BEFORE INSERT ON tide_push_outbox BEGIN SELECT RAISE(ABORT,'synthetic push failure'); END": ('synthetic_push_failure', 'tide_push_outbox', 'INSERT', None, 'synthetic push failure'),
            "CREATE TRIGGER media_test_ready_failure BEFORE UPDATE OF status ON bartide_menu_files WHEN NEW.tenant_id='db-failure' AND NEW.status='ready' BEGIN SELECT RAISE(ABORT,'synthetic transition failure'); END": ('media_test_ready_failure', 'bartide_menu_files', 'UPDATE OF status', "NEW.tenant_id='db-failure' AND NEW.status='ready'", 'synthetic transition failure'),
            "CREATE TRIGGER fail_session BEFORE UPDATE OF session_id ON tide_service_orders WHEN NEW.tenant_id='dbfail' BEGIN SELECT RAISE(ABORT,'synthetic fault'); END": ('fail_session', 'tide_service_orders', 'UPDATE OF session_id', "NEW.tenant_id='dbfail'", 'synthetic fault'),
            "CREATE TRIGGER fail_commission BEFORE INSERT ON tide_referral_sales BEGIN SELECT RAISE(ABORT,'synthetic commission fault'); END": ('fail_commission', 'tide_referral_sales', 'INSERT', None, 'synthetic commission fault'),
        }
        if statement in rollback_triggers:
            name, table, operation, predicate, message = rollback_triggers[statement]
            function = 'fixture_' + name
            body = pg_sql.Literal('BEGIN RAISE EXCEPTION ' + "'" + message.replace("'", "''") + "'; END;")
            self.connection.execute(pg_sql.SQL('CREATE FUNCTION {}() RETURNS trigger LANGUAGE plpgsql AS {}').format(pg_sql.Identifier(function), body))
            when = pg_sql.SQL(' WHEN (' + predicate + ')') if predicate else pg_sql.SQL('')
            self.connection.execute(pg_sql.SQL('CREATE TRIGGER {} BEFORE {} ON {} FOR EACH ROW{} EXECUTE FUNCTION {}()').format(pg_sql.Identifier(name), pg_sql.SQL(operation), pg_sql.Identifier(table), when, pg_sql.Identifier(function)))
            return FixtureCursor(rows=[])
        drop_triggers = {'DROP TRIGGER ' + row[0]: row[1] for row in rollback_triggers.values()}
        if statement in drop_triggers:
            name = statement.removeprefix('DROP TRIGGER ')
            self.connection.execute(pg_sql.SQL('DROP TRIGGER {} ON {}').format(pg_sql.Identifier(name), pg_sql.Identifier(drop_triggers[statement])))
            self.connection.execute(pg_sql.SQL('DROP FUNCTION {}()').format(pg_sql.Identifier('fixture_' + name)))
            return FixtureCursor(rows=[])
        if statement.strip().upper() == 'PRAGMA FOREIGN_KEY_CHECK':
            return FixtureCursor(rows=self.adapter.foreign_key_violations(self.connection))
        if statement == "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND (name LIKE 'bartide_%' OR name LIKE 'fit_%' OR name LIKE 'tide_%')":
            return FixtureCursor(self.connection.execute("SELECT COUNT(*) FROM pg_tables WHERE schemaname=%s AND (tablename LIKE 'bartide_%%' OR tablename LIKE 'fit_%%' OR tablename LIKE 'tide_%%')", (self.adapter.schema,)))
        if statement == "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=?":
            return FixtureCursor(self.connection.execute('SELECT COUNT(*) FROM pg_tables WHERE schemaname=%s AND tablename=%s', (self.adapter.schema, values[0])))
        if statement == 'SELECT * FROM tide_feature_migrations ORDER BY version':
            return FixtureCursor(self.connection.execute('SELECT * FROM tide_postgres_migrations ORDER BY version'))
        if statement == "UPDATE tide_business_posts SET published_at=? WHERE julianday(published_at)>julianday('now','-1 hour')":
            statement = "UPDATE tide_business_posts SET published_at=? WHERE tide_iso_instant(published_at)>statement_timestamp()-interval '1 hour'"
        history_queries = {
            'SELECT COUNT(*) FROM tide_feature_migrations': ['tide_staff_course_assignments'],
            'SELECT COUNT(*) FROM tide_feature_migrations WHERE version=9': ['tide_sales_history', 'tide_sales_operations', 'tide_sales_demo_links'],
            "SELECT COUNT(*) FROM tide_feature_migrations WHERE resource_name LIKE '%0003_events.sql'": ['tide_events', 'tide_event_rsvps', 'tide_event_rsvp_requests'],
        }
        if statement in history_queries:
            # PostgreSQL installs those features in one hashed baseline. Check its
            # real ledger and feature objects; do not fabricate old SQLite history.
            return FixtureCursor(self.connection.execute('''
                SELECT COUNT(*) FROM tide_postgres_migrations
                WHERE version=1 AND resource_name LIKE %s AND length(sha256)=64
                  AND length(manifest_sha256)=64 AND length(schema_sha256)=64
                  AND NOT EXISTS(SELECT 1 FROM unnest(%s::text[]) item WHERE to_regclass(item) IS NULL)
                ''', ('%0001_baseline.sql', history_queries[statement])))
        if re.search(r'\b(?:PRAGMA|INSERT\s+OR|REPLACE\s+INTO|COLLATE\s+NOCASE|julianday|json_extract|sqlite_master)\b', statement, re.I):
            raise NotImplementedError('A fixture statement needs an explicit PostgreSQL equivalent')
        # SQLite fixture booleans are stored as 0/1, matching the migrated BIGINT
        # flags. Keep that fixture input contract when psycopg binds parameters.
        parameters = tuple(int(value) if isinstance(value, bool) else value for value in values)
        return FixtureCursor(self.connection.execute(qmark_parameters(statement), parameters))

    def executemany(self, statement, rows):
        cursor = None
        for values in rows:
            cursor = self.execute(statement, values)
        return cursor or FixtureCursor(rows=[])


class ProcessAdapter:
    def __init__(self, owner):
        self.owner = owner

    def __getattr__(self, name):
        return getattr(subprocess, name)

    def Popen(self, args, *positional, **keywords):
        binary = next((Path(str(arg)) for arg in args if str(arg).endswith(('TideCasa.Api.dll', 'TideCasa.Blazor.dll'))), None)
        if binary is None:
            raise RuntimeError('PostgreSQL feature fixtures may launch only the existing API or Web binary')
        # Some legacy suites copy ROOT instead of TIDE_TEST_BUILD_ROOT. Always
        # launch the immutable artifact selected by this runner, never a live build.
        binary = self.owner.build / binary.stem / 'bin/Debug/net10.0' / binary.name
        args = [str(binary) if str(arg).endswith(('TideCasa.Api.dll', 'TideCasa.Blazor.dll')) else arg for arg in args]
        env = dict(keywords.get('env') or os.environ)
        self.owner.configure_environment(env, binary.name == 'TideCasa.Api.dll')
        actual_hash = hashlib.sha256(binary.read_bytes()).hexdigest()
        if actual_hash != self.owner.expected_hashes[binary.name]:
            raise RuntimeError('Compiled binary changed during PostgreSQL verification; restart the suite')
        self.owner.binaries[binary.name] = actual_hash
        keywords['env'] = env
        if os.name == 'nt':
            keywords['creationflags'] = subprocess.CREATE_NO_WINDOW
        process = subprocess.Popen(args, *positional, **keywords)
        self.owner.processes.append(process)
        if self.owner.suite == 'media' and binary.name == 'TideCasa.Api.dll' and env.get('Auth__Enabled') == 'false' and env.get('Media__Provider') == 'local':
            # Only the original, explicitly negative local-media startup probes.
            # Their assertions stay unchanged; a slow Windows/PG cold start gets
            # one bounded measured allowance before their existing 20s check.
            started = time.monotonic()
            try:
                process.wait(timeout=120)
                exited = True
            except subprocess.TimeoutExpired:
                exited = False
            self.owner.startup_allowances.append({'purpose': 'Original ' + self.owner.suite + ' negative-startup probe',
                'environment': env['ASPNETCORE_ENVIRONMENT'], 'allowanceSeconds': 120,
                'elapsedSeconds': round(time.monotonic() - started, 3), 'exitedWithinAllowance': exited})
        return process


class CopyAdapter:
    """Reuse immutable binaries without multiplying the Windows disk footprint."""
    def __init__(self, owner):
        self.owner = owner

    def __getattr__(self, name):
        return getattr(shutil, name)

    def copytree(self, source, destination, **options):
        source, destination = Path(source).resolve(), Path(destination).resolve()
        project = next((name for name in ('TideCasa.Api', 'TideCasa.Blazor') if name in source.parts), None)
        allowed = [Path(namespace['RUN']).resolve() for namespace in self.owner.namespaces if 'RUN' in namespace]
        if project is None or not any(destination.is_relative_to(run) and destination != run for run in allowed):
            raise RuntimeError('Fixture copies must stay inside an owned evidence directory')
        source = self.owner.build / project / 'bin/Debug/net10.0'
        def hardlink(original, copied):
            os.link(original, copied)
            return copied
        result = shutil.copytree(source, destination, copy_function=hardlink, **options)
        self.owner.copies.append(destination)
        return result


class PostgresFixture:
    def __init__(self, suite):
        supplied = json.loads((ROOT / '.tools/postgresql-17-test/fixture.json').read_text(encoding='utf-8-sig'))
        if supplied['host'] not in ('127.0.0.1', 'localhost', '::1') or not 1024 <= int(supplied['port']) <= 65535:
            raise RuntimeError('A loopback PostgreSQL test server is required')
        self.credentials = {key: supplied[key] for key in ('host', 'port', 'user', 'password')}
        self.credentials['dbname'] = supplied['database']
        # Seed/inspection connections stay on the original loopback transport;
        # Production application launches separately require VerifyFull below.
        self.credentials['sslmode'] = 'disable'
        self.suite = suite
        self.storage_boundary_exceptions = []
        self.startup_allowances = []
        self.schema = 'tide_test_' + re.sub('[^a-z0-9]', '', suite)[:20] + '_' + uuid.uuid4().hex[:16]
        self.owned = False
        self.processes, self.namespaces, self.binaries, self.copies = [], [], {}, []
        self.fixture_available, self.fixture_connections = [], []
        self.fixture_lock = threading.Lock()
        self.fixture_slots = threading.BoundedSemaphore(8)
        self.build = Path(os.environ.get('TIDE_TEST_BUILD_ROOT', ROOT)).resolve()
        self.expected_hashes = {project + '.dll': hashlib.sha256((self.build / project / 'bin/Debug/net10.0' / (project + '.dll')).read_bytes()).hexdigest()
                                for project in ('TideCasa.Api', 'TideCasa.Blazor')}
        with psycopg.connect(**self.credentials, autocommit=True) as connection:
            self.server_version = connection.info.server_version
            connection.execute(pg_sql.SQL('CREATE SCHEMA {}').format(pg_sql.Identifier(self.schema)))
        self.owned = True
        self.storage = SimpleNamespace(connect=self.fixture_connection, IntegrityError=psycopg.IntegrityError)
        self.subprocess = ProcessAdapter(self)
        self.shutil = CopyAdapter(self)

    def connect(self):
        return psycopg.connect(**self.credentials, options='-c search_path=' + self.schema + ' -c timezone=UTC -c lock_timeout=10000')

    def checkout_fixture_connection(self):
        # Seed and inspection calls retain separate committed transactions. Reuse
        # only their idle transport; the API has its own independent connections.
        if not self.fixture_slots.acquire(timeout=60):
            raise TimeoutError('Fixture connection capacity exhausted')
        try:
            with self.fixture_lock:
                if self.fixture_available:
                    return self.fixture_available.pop()
            connection = self.connect()
            with self.fixture_lock:
                self.fixture_connections.append(connection)
            return connection
        except BaseException:
            self.fixture_slots.release()
            raise

    def return_fixture_connection(self, connection):
        if connection.closed or connection.broken:
            connection.close()
        else:
            with self.fixture_lock:
                self.fixture_available.append(connection)
        self.fixture_slots.release()

    def fixture_connection(self, database, **options):
        # The SQLite path is only a fixture identity. It is never opened or copied.
        if Path(database).name != 'synthetic.db':
            raise RuntimeError('Unexpected fixture database identity')
        return FixtureConnection(self)

    def install_namespace(self, namespace):
        if any(existing is namespace for existing in self.namespaces):
            return
        self.namespaces.append(namespace)
        namespace['sqlite3'] = self.storage
        namespace['subprocess'] = self.subprocess
        namespace['shutil'] = self.shutil
        # A few unchanged suites import the native-form helper via importlib and
        # call its launch function. Its function globals need the same explicit
        # process boundary; otherwise that helper could start a SQLite host.
        helper = (ROOT / 'scripts/verify-management-web.py').resolve()
        for value in list(namespace.values()):
            if isinstance(value, ModuleType) and getattr(value, '__file__', None) and Path(value.__file__).resolve() == helper:
                self.install_namespace(vars(value))

    def configure_environment(self, env, is_api):
        environment = env.get('ASPNETCORE_ENVIRONMENT', 'Development')
        if environment not in ('Development', 'Production'):
            raise RuntimeError('Unexpected fixture hosting environment')
        family = {'media': 'Media__', 'business-posts': 'WebPush__', 'service-billing': 'ServiceBilling__',
                  'merchant-payments': 'MerchantPayments__'}.get(self.suite)
        fixture_provider = {name: value for name, value in env.items() if family and name.startswith(family)}
        # Only the suite explicitly exercising a local fake may retain its own
        # provider configuration, including its original fail-closed variants.
        if self.suite == 'service-billing':
            key = fixture_provider.get('ServiceBilling__RestrictedKey', '')
            endpoint = fixture_provider.get('ServiceBilling__DevelopmentApiBase', '')
            if key not in ('', 'rk_test_tide_local_fixture') or endpoint and urlsplit(endpoint).hostname not in ('127.0.0.1', 'localhost', '::1'):
                raise RuntimeError('Only the synthetic service-billing provider is allowed')
        if self.suite == 'merchant-payments':
            key = fixture_provider.get('MerchantPayments__RestrictedKey', '')
            endpoint = fixture_provider.get('MerchantPayments__ApiBaseUrl', '')
            allowed_negative = endpoint == 'http://192.0.2.1:9999'
            if key not in ('', 'rk_test_synthetic_000000000', 'rk_live_synthetic_000000000') or endpoint and not allowed_negative and urlsplit(endpoint).hostname not in ('127.0.0.1', 'localhost', '::1'):
                raise RuntimeError('Only the synthetic merchant-payment provider is allowed')
        if self.suite == 'business-posts':
            endpoint = fixture_provider.get('WebPush__DevelopmentPushOrigin', '')
            if endpoint and endpoint != 'http://example.invalid' and urlsplit(endpoint).hostname not in ('127.0.0.1', 'localhost', '::1'):
                raise RuntimeError('Only the synthetic push provider or original invalid-origin probe is allowed')
        for name in list(env):
            upper = name.upper()
            if upper.startswith(('CONNECTIONSTRINGS__', 'STORAGE__')):
                env.pop(name)
            elif any(word in upper for word in ('CLOUDFLARE', 'AWS_ACCESS', 'AWS_SECRET', 'STRIPE__SECRET', 'STRIPE__APIKEY', 'STRIPE__WEBHOOK', 'MERCHANTPAYMENTS__SECRET', 'SERVICEBILLING__SECRET')):
                env.pop(name)
            elif upper.startswith(('SERVICEBILLING__', 'MERCHANTPAYMENTS__', 'MEDIA__', 'WEBPUSH__')):
                env.pop(name)
        if env.get('Notifications__Mode') != 'local-test':
            for name in list(env):
                if name.upper().startswith('NOTIFICATIONS__') or 'RESEND' in name.upper():
                    env.pop(name)
            env['Notifications__Mode'] = 'disabled'
        for name in ('Auth__SupabaseUrl', 'Notifications__ApiBaseUrl', 'Notifications__ResendApiBaseUrl'):
            if env.get(name) and urlsplit(env[name]).hostname not in ('127.0.0.1', 'localhost', '::1'):
                raise RuntimeError('A fixture provider URL must use loopback')
        if is_api and not env.get('Auth__SupabaseUrl') and env.get('Auth__Enabled') != 'false':
            raise RuntimeError('Synthetic identity-provider configuration is required')
        # Keep the suite's explicit loopback mail fake for notification tests.
        # All money, object-storage and push capabilities stay disabled.
        env.update({'ASPNETCORE_ENVIRONMENT': environment, 'DOTNET_ENVIRONMENT': environment,
                    'DOTNET_PROCESSOR_COUNT': '1',
                    'Storage__Provider': 'PostgreSql', 'Storage__PostgresSchema': self.schema,
                    'Storage__CreatePostgresSchema': 'false', 'Auth__AllowLocalTestProvider': 'true',
                    'Stripe__CheckoutEnabled': 'false', 'Stripe__InvoicesEnabled': 'false',
                    'ServiceBilling__CheckoutEnabled': 'false', 'ServiceBilling__RestrictedKey': '',
                    'MerchantPayments__CheckoutEnabled': 'false', 'MerchantPayments__RestrictedKey': '',
                    'MerchantPayments__OnboardingEnabled': 'false', 'WebPush__Enabled': 'false',
                    'Media__Provider': 'disabled'})
        env.update(fixture_provider)
        if self.suite == 'media':
            run = next(Path(namespace['RUN']).resolve() for namespace in self.namespaces if 'RUN' in namespace)
            if not run.is_relative_to((ROOT / '.tools/media-verification').resolve()):
                raise RuntimeError('Media scratch storage must stay in its owned fixture')
            # Preserve the original assertion's actual spool location instead of
            # letting PostgreSQL's host-wide temp fallback make that check vacuous.
            env['Media__SpoolPath'] = str(run / 'media-spool')
        def quoted(value):
            return '"' + str(value).replace('"', '""') + '"'
        certificate = ROOT / '.tools/postgresql-17-test/tls/server.crt'
        production_tls = environment == 'Production' and certificate.is_file()
        transport = ('SSL Mode=VerifyFull;Root Certificate=' + quoted(certificate)) if production_tls else 'SSL Mode=Disable'
        env['ConnectionStrings__Application'] = ';'.join(name + '=' + quoted(self.credentials[key])
            for name, key in [('Host', 'host'), ('Port', 'port'), ('Database', 'dbname'), ('Username', 'user'), ('Password', 'password')]) + ';' + transport + ';Pooling=true;Maximum Pool Size=8'
        if environment == 'Production' and not production_tls:
            raise RuntimeError('Production persisted workflows require the trusted local PostgreSQL TLS fixture')

    def foreign_key_violations(self, connection):
        constraints = connection.execute('''
            SELECT c.oid,n.nspname,t.relname,pn.nspname,p.relname,c.conname,c.convalidated,
              ARRAY(SELECT a.attname FROM unnest(c.conkey) WITH ORDINALITY k(num,ord)
                    JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.num ORDER BY k.ord),
              ARRAY(SELECT a.attname FROM unnest(c.confkey) WITH ORDINALITY k(num,ord)
                    JOIN pg_attribute a ON a.attrelid=c.confrelid AND a.attnum=k.num ORDER BY k.ord)
            FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid JOIN pg_namespace n ON n.oid=t.relnamespace
            JOIN pg_class p ON p.oid=c.confrelid JOIN pg_namespace pn ON pn.oid=p.relnamespace
            WHERE c.contype='f' AND n.nspname=%s
            ''', (self.schema,)).fetchall()
        if not constraints:
            raise AssertionError('PostgreSQL fixture has no foreign-key constraints')
        violations = []
        for _, namespace, table, parent_namespace, parent, name, valid, columns, parent_columns in constraints:
            if namespace != self.schema or parent_namespace != self.schema:
                raise AssertionError('A fixture foreign key crosses its owned schema boundary')
            nonnull = pg_sql.SQL(' AND ').join(pg_sql.SQL('c.{} IS NOT NULL').format(pg_sql.Identifier(column)) for column in columns)
            matches = pg_sql.SQL(' AND ').join(pg_sql.SQL('p.{}=c.{}').format(pg_sql.Identifier(p), pg_sql.Identifier(c)) for c, p in zip(columns, parent_columns))
            query = pg_sql.SQL('SELECT COUNT(*) FROM {}.{} c WHERE {} AND NOT EXISTS(SELECT 1 FROM {}.{} p WHERE {})').format(
                pg_sql.Identifier(namespace), pg_sql.Identifier(table), nonnull, pg_sql.Identifier(parent_namespace), pg_sql.Identifier(parent), matches)
            count = connection.execute(query).fetchone()[0]
            if count or not valid:
                violations.append((table, name, count, valid))
        return violations

    def prime_media_startup_schema(self, namespace):
        """Restore the initialized-database precondition of the final media probes."""
        support = namespace['s']
        origin = 'http://127.0.0.1:' + str(support.port())
        run = Path(namespace['RUN']).resolve()
        log = (run / 'postgres-startup-probe-setup.log').open('w', encoding='utf-8')
        support.LOGS.append(log)
        binary = self.build / 'TideCasa.Api/bin/Debug/net10.0/TideCasa.Api.dll'
        env = dict(os.environ, ASPNETCORE_ENVIRONMENT='Development', ASPNETCORE_URLS=origin,
                   Auth__Enabled='false', Media__Provider='disabled')
        process = self.subprocess.Popen([str(support.SDK), str(binary)], cwd=ROOT / 'TideCasa.Api',
            env=env, stdout=log, stderr=subprocess.STDOUT)
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        deadline = time.monotonic() + 150
        try:
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise RuntimeError('Media startup-probe schema initialization stopped')
                try:
                    with opener.open(origin + '/health', timeout=2) as response:
                        if response.status == 200:
                            return
                except OSError:
                    pass
                time.sleep(.2)
            raise TimeoutError('Media startup-probe schema initialization timed out')
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=15)

    def results(self):
        seen, records = set(), []
        for namespace in self.namespaces:
            for key in ('RESULTS', 'results'):
                rows = namespace.get(key)
                if isinstance(rows, list) and id(rows) not in seen:
                    seen.add(id(rows))
                    records.extend(row for row in rows if isinstance(row, dict) and 'passed' in row)
        return records

    def evidence_directories(self):
        return sorted({str(namespace['RUN']) for namespace in self.namespaces if 'RUN' in namespace})

    def cleanup(self):
        for process in self.processes:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=10)
        for connection in self.fixture_connections:
            connection.close()
        for copied in self.copies:
            allowed = [Path(namespace['RUN']).resolve() for namespace in self.namespaces if 'RUN' in namespace]
            resolved = copied.resolve()
            if not resolved.is_relative_to((ROOT / '.tools').resolve()) or not any(resolved.is_relative_to(run) and resolved != run for run in allowed):
                raise RuntimeError('Refusing fixture-copy cleanup outside owned evidence')
            if copied.exists():
                shutil.rmtree(copied)
        if self.owned:
            if not re.fullmatch(r'tide_test_[a-z0-9]+_[a-f0-9]{16}', self.schema):
                raise RuntimeError('Refusing cleanup outside the schema owned by this fixture')
            with psycopg.connect(**self.credentials, autocommit=True) as connection:
                connection.execute(pg_sql.SQL('DROP SCHEMA {} CASCADE').format(pg_sql.Identifier(self.schema)))
            self.owned = False
