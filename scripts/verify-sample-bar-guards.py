"""Ensure the local identity-switching hub cannot be enabled in Production."""
from datetime import datetime, timezone
import importlib.util
import json
from pathlib import Path
import socket
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('sample', ROOT / 'scripts/run-sample-bar.py')
s = importlib.util.module_from_spec(spec); spec.loader.exec_module(s)
run = s.RUN / ('guard-check-' + datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S'))
s.RUN = run
results = []
try:
    for label, environment, enabled, address in [('flag-off','Development','false',None), ('production','Production','true',None), ('remote-client','Development','true','203.0.113.90')]:
        with socket.socket() as probe:
            probe.bind(('127.0.0.1',0)); port = probe.getsockname()[1]
        url = 'http://127.0.0.1:' + str(port)
        env = s.environment(1)
        env.update({'ASPNETCORE_ENVIRONMENT':environment,'SampleBar__Enabled':enabled,
                    'Api__BaseUrl':'https://api.example.invalid/', 'DataProtection__KeysPath':str(run / 'keys')})
        s.launch('TideCasa.Blazor', url, env, run / label)
        request = urllib.request.Request(url + '/sample-bar', headers={'X-Forwarded-For':address} if address else {})
        try: response = urllib.request.build_opener(urllib.request.ProxyHandler({})).open(request)
        except urllib.error.HTTPError as error: response = error
        with response:
            body = response.read().decode('utf-8')
            passed = response.status == 404 and s.BAR['password'] not in body and 'Try Casey' not in body and 'Switch sample person' not in body
        results.append({'case':label,'environment':environment,'enabled':enabled,'passed':passed,'status':response.status})
        print(('PASS ' if passed else 'FAIL ') + label + ': local sample hub hidden',flush=True)
        if not passed: raise AssertionError('Local-only sample boundary')
        s.PROCESSES[-1].terminate(); s.PROCESSES[-1].wait(timeout=20)
finally:
    for process in s.PROCESSES:
        if process.poll() is None: process.terminate(); process.wait(timeout=20)
    for log in s.LOGS: log.close()
    run.mkdir(parents=True,exist_ok=True)
    (run / 'results.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
