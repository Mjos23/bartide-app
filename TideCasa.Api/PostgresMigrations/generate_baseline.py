"""Reproduce the reviewed PostgreSQL baseline from immutable SQLite DDL only.

No application database is opened. SQLite runs in memory to fold ALTER statements;
generated artifacts contain schema metadata, never customer rows. After deployment,
do not regenerate version 1: add a new reviewed migration instead.
"""
from collections import defaultdict
import hashlib
import json
from pathlib import Path
import re
import sqlite3

HERE = Path(__file__).resolve().parent
API = HERE.parent
BOOTSTRAP = """
CREATE TABLE tide_schema_migrations (
 version INTEGER PRIMARY KEY, resource_name TEXT NOT NULL UNIQUE, source_path TEXT NOT NULL,
 sha256 TEXT NOT NULL, manifest_sha256 TEXT NOT NULL, applied_at TEXT NOT NULL
);
CREATE TABLE demo_requests (
 id TEXT PRIMARY KEY, payload_hash TEXT NOT NULL, email_hash TEXT NOT NULL,
 request_json TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'requested',
 notification_state TEXT NOT NULL DEFAULT 'pending', created_at TEXT NOT NULL
);
CREATE INDEX demo_requests_email_time ON demo_requests(email_hash,created_at);
CREATE TABLE tide_feature_migrations (
 version INTEGER PRIMARY KEY, resource_name TEXT NOT NULL UNIQUE, sha256 TEXT NOT NULL, applied_at TEXT NOT NULL
);
"""
KEY_RING = """
CREATE TABLE tide_data_protection_keys (
 id TEXT PRIMARY KEY, friendly_name TEXT NOT NULL, ciphertext TEXT NOT NULL, created_at TEXT NOT NULL
);
"""
HELPERS = r"""
-- These parsing helpers never change the preserved text columns. No dynamic SQL or elevated privileges.
-- Restrict timestamps to ISO dates/explicit-offset instants before the PostgreSQL cast:
-- PostgreSQL's special values (infinity, tomorrow, now, etc.) are deliberately not accepted.
CREATE FUNCTION tide_iso_instant(input_text TEXT) RETURNS TIMESTAMPTZ
LANGUAGE plpgsql STABLE STRICT PARALLEL SAFE SECURITY INVOKER
SET search_path = pg_catalog
AS $tide_iso$
BEGIN
    IF input_text ~ '^[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])$' THEN
        RETURN (input_text || 'T00:00:00Z')::TIMESTAMPTZ;
    END IF;
    IF input_text !~ '^[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])[Tt ](0[0-9]|1[0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]{1,7})?([Zz]|[+-]((0[0-9]|1[0-3]):[0-5][0-9]|14:00))$' THEN
        RETURN NULL;
    END IF;
    RETURN input_text::TIMESTAMPTZ;
EXCEPTION WHEN data_exception THEN
    RETURN NULL;
END;
$tide_iso$;

-- JSONB parsing is deterministic; invalid JSON, unsupported escapes and out-of-range
-- JSON numbers yield SQL NULL. The valid JSON literal null remains JSONB null.
CREATE FUNCTION tide_json(input_text TEXT) RETURNS JSONB
LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE SECURITY INVOKER
SET search_path = pg_catalog
AS $tide_json$
BEGIN
    RETURN input_text::JSONB;
EXCEPTION WHEN data_exception THEN
    RETURN NULL;
END;
$tide_json$;
"""


def quoted(value):
    assert re.fullmatch('[a-z][a-z0-9_]*', value), value
    assert len(value.encode()) <= 63, value
    return '"' + value + '"'


def checksum(value):
    return hashlib.sha256(value).hexdigest()


def check_expressions(sql):
    expressions = []
    for match in re.finditer(r'\bCHECK\s*\(', sql, re.I):
        start = cursor = match.end()
        depth, in_string = 1, False
        while depth:
            char = sql[cursor]
            if char == "'":
                if in_string and sql[cursor + 1:cursor + 2] == "'":
                    cursor += 2
                    continue
                in_string = not in_string
            elif not in_string:
                depth += (char == '(') - (char == ')')
            cursor += 1
        expressions.append(sql[start:cursor - 1])
    return expressions


def main():
    source_manifest = json.loads((API / 'Migrations/manifest.json').read_text(encoding='utf-8'))
    legacy = sorted((API / 'Migrations').glob('*.sql'))
    features = sorted((API / 'FeatureMigrations').glob('*.sql'))
    assert len(legacy) == 14 and len(features) == 9
    for path, expected in zip(legacy, source_manifest['migrations'], strict=True):
        assert checksum(path.read_bytes()) == expected['sha256'], 'Immutable source mismatch: ' + path.name
    sources = [{'path': str(path.relative_to(API)).replace('\\', '/'), 'sha256': checksum(path.read_bytes())}
               for path in legacy + features]
    db = sqlite3.connect(':memory:')
    db.execute('PRAGMA foreign_keys=ON')
    for path in legacy:
        db.executescript(path.read_text(encoding='utf-8'))
    db.executescript(BOOTSTRAP)
    for path in features:
        db.executescript(path.read_text(encoding='utf-8'))
    original_tables = db.execute("SELECT name,sql FROM sqlite_schema WHERE type='table' ORDER BY name").fetchall()
    assert len(original_tables) == 76
    assert not any('AUTOINCREMENT' in sql.upper() for _, sql in original_tables)
    db.executescript(KEY_RING)
    tables = db.execute("SELECT name,sql FROM sqlite_schema WHERE type='table' ORDER BY name").fetchall()
    output = ['-- PostgreSQL baseline 1. Schema only; no customer records or legacy history are fabricated.',
              '-- Execute only through PostgresSchemaMigrator in the isolated application search_path.',
              '-- TEXT retains original timestamps/JSON/IDs; BIGINT retains SQLite signed 64-bit integers.',
              '-- C collation preserves binary comparisons. Referral NOCASE uniqueness folds ASCII only.',
              '-- No SQLite AUTOINCREMENT is present. Legacy migration versions are explicit, not identities.', '']
    indexes, foreign_keys, inventory, dependencies = [], [], [], {}
    total_checks = total_foreign_keys = total_uniques = 0
    for name, source_sql in tables:
        columns = db.execute('PRAGMA table_info(' + quoted(name) + ')').fetchall()
        definitions, constraints, column_inventory = [], [], []
        primary = [row[1] for row in sorted(columns, key=lambda row: row[5]) if row[5]]
        for _, column, kind, not_null, default, pk in columns:
            assert kind.upper() in ('TEXT', 'INTEGER'), (name, column, kind)
            pg_type = 'text' if kind.upper() == 'TEXT' else 'bigint'
            declaration = quoted(column) + ' ' + ('TEXT COLLATE "C"' if pg_type == 'text' else 'BIGINT')
            if not_null or pk:
                declaration += ' NOT NULL'
            if default is not None:
                assert re.fullmatch(r"'(?:[^']|'')*'|-?[0-9]+|NULL", default, re.I), default
                declaration += ' DEFAULT ' + default
            definitions.append(declaration)
            column_inventory.append({'name': column, 'type': pg_type, 'notNull': bool(not_null or pk),
                'sqliteNotNull': bool(not_null), 'default': default, 'primaryKeyOrdinal': pk})
        if primary:
            constraint = 'pgpk_' + name
            definitions.append('CONSTRAINT ' + quoted(constraint) + ' PRIMARY KEY (' + ', '.join(map(quoted, primary)) + ')')
            constraints.append({'name': constraint, 'kind': 'p', 'columns': primary})
        for number, expression in enumerate(check_expressions(source_sql), 1):
            constraint = f'pgck_{name}_{number:02d}'
            definitions.append('CONSTRAINT ' + quoted(constraint) + ' CHECK (' + expression + ')')
            constraints.append({'name': constraint, 'kind': 'c', 'expression': expression})
            total_checks += 1
        table_indexes = []
        unique_number = 0
        for _, index_name, unique, origin, partial in sorted(db.execute('PRAGMA index_list(' + quoted(name) + ')').fetchall(), key=lambda row: row[1]):
            if origin == 'pk':
                continue
            parts = [row for row in db.execute('PRAGMA index_xinfo("' + index_name.replace('"', '""') + '")') if row[5]]
            assert all(row[1] >= 0 and row[2] for row in parts), index_name
            index_columns = [row[2] for row in parts]
            nocase = any(row[4] == 'NOCASE' for row in parts)
            if nocase:
                assert name == 'tide_referral_profiles' and index_columns in (['email'], ['code']) and unique and origin == 'u'
                index_name = 'pgux_tide_referral_profiles_' + index_columns[0] + '_nocase'
                expression = "translate(" + quoted(index_columns[0]) + ", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') COLLATE \"C\""
                indexes.append('CREATE UNIQUE INDEX ' + quoted(index_name) + ' ON ' + quoted(name) + ' (' + expression + ');')
                table_indexes.append({'name': index_name, 'unique': True, 'columns': index_columns, 'asciiNoCase': True, 'predicate': None})
                total_uniques += 1
            elif origin == 'u':
                unique_number += 1
                constraint = f'pguq_{name}_{unique_number:02d}'
                definitions.append('CONSTRAINT ' + quoted(constraint) + ' UNIQUE (' + ', '.join(map(quoted, index_columns)) + ')')
                constraints.append({'name': constraint, 'kind': 'u', 'columns': index_columns})
                total_uniques += 1
            else:
                assert origin == 'c' and all(row[4] == 'BINARY' for row in parts), index_name
                original_index_sql = db.execute('SELECT sql FROM sqlite_schema WHERE name=?', (index_name,)).fetchone()[0]
                where = re.search(r'\bWHERE\s+(.+)\Z', original_index_sql, re.I | re.S)
                predicate = where.group(1).strip() if where else None
                assert bool(predicate) == bool(partial)
                declaration = 'CREATE ' + ('UNIQUE ' if unique else '') + 'INDEX ' + quoted(index_name) + ' ON ' + quoted(name)
                declaration += ' (' + ', '.join(quoted(row[2]) + (' DESC' if row[3] else '') for row in parts) + ')'
                if predicate:
                    declaration += ' WHERE ' + predicate
                indexes.append(declaration + ';')
                table_indexes.append({'name': index_name, 'unique': bool(unique), 'columns': index_columns, 'asciiNoCase': False, 'predicate': predicate})
                total_uniques += int(unique)
        groups = defaultdict(list)
        for row in db.execute('PRAGMA foreign_key_list(' + quoted(name) + ')'):
            groups[row[0]].append(row)
        dependencies[name] = set()
        for number, group in enumerate(sorted(groups.values(), key=lambda rows: (rows[0][2], rows[0][3])), 1):
            group.sort(key=lambda row: row[1])
            _, _, target, _, _, on_update, on_delete, match = group[0]
            assert match == 'NONE' and on_update in ('NO ACTION', 'CASCADE', 'RESTRICT', 'SET NULL', 'SET DEFAULT') and on_delete in ('NO ACTION', 'CASCADE', 'RESTRICT', 'SET NULL', 'SET DEFAULT')
            local, remote = [row[3] for row in group], [row[4] for row in group]
            constraint = f'pgfk_{name}_{number:02d}'
            foreign_keys.append('ALTER TABLE ' + quoted(name) + ' ADD CONSTRAINT ' + quoted(constraint)
                + ' FOREIGN KEY (' + ', '.join(map(quoted, local)) + ') REFERENCES ' + quoted(target)
                + ' (' + ', '.join(map(quoted, remote)) + ') ON UPDATE ' + on_update + ' ON DELETE ' + on_delete + ' DEFERRABLE INITIALLY IMMEDIATE;')
            constraints.append({'name': constraint, 'kind': 'f', 'columns': local, 'references': target,
                'referencedColumns': remote, 'onUpdate': on_update, 'onDelete': on_delete, 'deferrable': True, 'initiallyDeferred': False})
            dependencies[name].add(target)
            total_foreign_keys += 1
        output.append('CREATE TABLE ' + quoted(name) + ' (\n    ' + ',\n    '.join(definitions) + '\n);\n')
        inventory.append({'name': name, 'columns': column_inventory, 'constraints': constraints, 'indexes': table_indexes})
    output += ['-- Existing named and partial indexes, plus exact ASCII-NOCASE uniqueness.', *indexes, '',
               '-- Apply references after every table exists; legacy SQLite DDL contains forward references.', *foreign_keys, '', HELPERS]
    import_order = []
    pending = {name: set(targets) for name, targets in dependencies.items()}
    while pending:
        ready = sorted(name for name, targets in pending.items() if not targets)
        assert ready, 'Foreign-key cycle needs an explicitly reviewed import strategy.'
        import_order.extend(ready)
        for name in ready:
            pending.pop(name)
        for targets in pending.values():
            targets.difference_update(ready)
    baseline = '\n'.join(output)
    destination = HERE / '0001_baseline.sql'
    destination.write_text(baseline, encoding='utf-8', newline='\n')
    manifest = {'formatVersion': 1, 'description': 'Folded immutable SQLite baseline, nine feature migrations and encrypted Blazor key storage.',
        'migrations': [{'version': 1, 'resourceName': 'TideCasa.Api.PostgresMigrations.0001_baseline.sql', 'sha256': checksum(destination.read_bytes())}],
        'sqliteSources': sources, 'sqliteBootstrapSha256': checksum(BOOTSTRAP.encode()),
        'sqliteApplicationTables': len(original_tables), 'tables': inventory, 'importOrder': import_order,
        'autoincrementTables': [], 'explicitVersionTables': ['tide_schema_migrations', 'tide_feature_migrations'],
        'functions': [{'name': 'tide_iso_instant', 'argumentTypes': 'text', 'returnType': 'timestamp with time zone', 'volatility': 's'},
                      {'name': 'tide_json', 'argumentTypes': 'text', 'returnType': 'jsonb', 'volatility': 'i'}],
        'counts': {'tables': len(inventory), 'columns': sum(len(table['columns']) for table in inventory),
                   'checks': total_checks, 'foreignKeys': total_foreign_keys, 'uniqueConstraintsAndIndexesExcludingPrimaryKeys': total_uniques,
                   'namedIndexesExcludingConstraintIndexes': len(indexes)}}
    (HERE / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8', newline='\n')
    print(json.dumps(manifest['counts'], indent=2))
    print('Schema-only baseline and manifest generated. No application database opened.')
    db.close()


if __name__ == '__main__':
    main()
