"""Run original feature workflows against isolated real PostgreSQL schemas.

Build API/Web first. This runner never builds, calls real providers, or touches
normal preview databases. The AST insertion installs test boundary adapters only;
it does not change workflows, seed values, or production source. The one literal
SQLite-ledger assertion is explicitly replaced by a real PostgreSQL-ledger check
and recorded in evidence. Focused media startup checks are separately labelled.
"""
import argparse
import ast
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import traceback

from postgres_test_support import PostgresFixture, ROOT

SUITES = ['auth', 'restaurant-ordering', 'restaurant-management', 'rewards',
          'events', 'staff-training', 'sales-pipeline', 'demo-inbox', 'workspace-registration']
ALL_SUITES = SUITES + ['referrals', 'launch-review', 'business-posts', 'media', 'service-billing',
                      'merchant-payments', 'rewards-web', 'management-web', 'media-guards']


def child(suite, destination):
    adapter, error, success, records = None, None, False, []
    source_suite = 'media' if suite == 'media-guards' else suite
    try:
        if os.name == 'nt':
            # The original negative-startup probes deliberately throw. Keep them
            # headless instead of opening Windows fault-reporting dialogs.
            import ctypes
            ctypes.windll.kernel32.SetErrorMode(0x0001 | 0x0002)
        adapter = PostgresFixture(source_suite)
        # Shared helpers retain their original implementations. Only their storage
        # and child-process boundary objects are substituted before suite imports.
        import restaurant_test_support as fixture
        adapter.install_namespace(vars(fixture))
        path = ROOT / 'scripts' / ('verify-' + source_suite + '.py')
        source = path.read_text(encoding='utf-8-sig')
        tree = ast.parse(source, filename=str(path))
        if source_suite == 'business-posts':
            expected = "check('Eighth additive migration has recorded source checksum', history[-1][0] == 8 and history[-1][2] == hashlib.sha256((s.ROOT/'TideCasa.Api/FeatureMigrations/0008_business_posts.sql').read_bytes()).hexdigest())"
            target = ast.dump(ast.parse(expected).body[0], include_attributes=False)
            run_function = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == 'run')
            matches = [i for i, node in enumerate(run_function.body) if ast.dump(node, include_attributes=False) == target]
            if len(matches) != 1:
                raise RuntimeError('The SQLite-specific business-post ledger assertion changed; review its explicit provider equivalent')
            replacement = ast.parse('_postgres_fixture.check_postgres_baseline_history(check, history)').body[0]
            run_function.body[matches[0]] = ast.copy_location(replacement, run_function.body[matches[0]])
            adapter.provider_assertion_adaptations.append({'original': 'Eighth additive migration has recorded source checksum',
                'replacement': 'PostgreSQL baseline has recorded source and manifest checksums',
                'reason': 'PostgreSQL uses one hashed version-1 baseline; it does not fabricate the SQLite migration ledger'})
        position = next(i for i, node in enumerate(tree.body) if isinstance(node, ast.Try) or
                        isinstance(node, ast.If) and isinstance(node.test, ast.Compare) and
                        isinstance(node.test.left, ast.Name) and node.test.left.id == '__name__')
        if suite == 'media-guards':
            run_function = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == 'run')
            probes = [node for node in run_function.body if isinstance(node, ast.Expr) and isinstance(node.value, ast.Call)
                      and isinstance(node.value.func, ast.Name) and node.value.func.id == 'startup_restriction']
            if len(probes) != 2 or not isinstance(tree.body[position], ast.Try):
                raise RuntimeError('The original media startup probe layout changed; review the focused runner')
            prime = ast.parse('_postgres_fixture.prime_media_startup_schema(globals())').body[0]
            tree.body[position].body = [prime, *probes]
        install = ast.parse('_postgres_fixture.install_namespace(globals())').body[0]
        tree.body.insert(position, install)
        ast.fix_missing_locations(tree)
        namespace = {'__name__': '__main__', '__file__': str(path), '_postgres_fixture': adapter}
        previous_args = sys.argv
        sys.argv = [str(path)]
        try:
            exec(compile(tree, str(path), 'exec'), namespace)
        finally:
            sys.argv = previous_args
        records = adapter.results()
        if not records or not all(row['passed'] for row in records):
            raise AssertionError('The unchanged suite did not complete passing assertions')
        success = True
    except BaseException as exception:
        error = type(exception).__name__
        # The traceback contains fixture code paths, never connection arguments.
        traceback.print_exc()
    finally:
        if adapter:
            records = adapter.results()
            evidence = {'suite': suite, 'passed': success, 'errorType': error,
                        'passedChecks': sum(bool(row['passed']) for row in records),
                        'failedChecks': [row['check'] for row in records if not row['passed']],
                        'assertions': records, 'postgresVersion': adapter.server_version,
                        'assertionSourceSha256': hashlib.sha256((ROOT / 'scripts' / ('verify-' + source_suite + '.py')).read_bytes()).hexdigest(),
                        'scope': 'Two unchanged media startup assertions only' if suite == 'media-guards' else 'Complete original workflow suite; provider metadata adaptations are listed separately',
                        'binarySha256': adapter.binaries, 'expectedBinarySha256': adapter.expected_hashes,
                        'evidenceDirectories': adapter.evidence_directories(), 'schema': adapter.schema,
                        'storageBoundaryExceptions': adapter.storage_boundary_exceptions,
                        'startupAllowances': adapter.startup_allowances,
                        'providerAssertionAdaptations': adapter.provider_assertion_adaptations}
            try:
                adapter.cleanup()
                evidence['schemaRemoved'] = True
            except BaseException as exception:
                evidence['schemaRemoved'] = False
                evidence['cleanupErrorType'] = type(exception).__name__
                success = False
            evidence['passed'] = success
            destination.write_text(json.dumps(evidence, indent=2), encoding='utf-8')
    return 0 if success else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--suites', nargs='+', choices=ALL_SUITES, default=SUITES)
    parser.add_argument('--child', choices=ALL_SUITES)
    parser.add_argument('--result', type=Path)
    parser.add_argument('--snapshot', type=Path, help='Reuse an immutable snapshot created by an earlier run')
    options = parser.parse_args()
    if options.child:
        return child(options.child, options.result)
    run = ROOT / '.tools/postgres-feature-verification' / datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
    run.mkdir(parents=True, exist_ok=False)
    source = Path(os.environ.get('TIDE_TEST_BUILD_ROOT', ROOT)).resolve()
    snapshot = options.snapshot.resolve() if options.snapshot else run / 'build'
    if options.snapshot and (not snapshot.is_relative_to((ROOT / '.tools/postgres-feature-verification').resolve()) or snapshot.name != 'build'):
        raise ValueError('Reuse only a snapshot owned by this PostgreSQL runner')
    def ignored(directory, names):
        return [name for name in names if name.endswith('.pdb') or (Path(directory).name == 'runtimes' and name != 'win-x64')]
    if not options.snapshot:
        for project in ('TideCasa.Api', 'TideCasa.Blazor'):
            shutil.copytree(source / project / 'bin/Debug/net10.0', snapshot / project / 'bin/Debug/net10.0',
                            ignore=ignored)
    child_environment = dict(os.environ, TIDE_TEST_BUILD_ROOT=str(snapshot))
    print(('Reusing' if options.snapshot else 'Copied') + ' immutable API/Web artifacts for PostgreSQL verification.', flush=True)
    summaries = []
    for suite in options.suites:
        result = run / (suite + '.json')
        with (run / (suite + '.log')).open('w', encoding='utf-8') as output:
            process = subprocess.run([sys.executable, str(Path(__file__).resolve()), '--child', suite, '--result', str(result)],
                cwd=ROOT, stdin=subprocess.DEVNULL, stdout=output, stderr=subprocess.STDOUT,
                env=child_environment,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        evidence = json.loads(result.read_text(encoding='utf-8')) if result.exists() else {'suite': suite, 'passed': False, 'errorType': 'NoResult'}
        summaries.append(evidence)
        print(('PASS ' if evidence['passed'] else 'FAIL ') + suite + ': ' + str(evidence.get('passedChecks', 0)) + ' checks', flush=True)
        if process.returncode:
            print('Failure evidence: ' + str(result), flush=True)
            break
    (run / 'results.json').write_text(json.dumps(summaries, indent=2), encoding='utf-8')
    print('PostgreSQL feature evidence: ' + str(run), flush=True)
    return 0 if len(summaries) == len(options.suites) and all(row['passed'] for row in summaries) else 1


if __name__ == '__main__':
    raise SystemExit(main())
