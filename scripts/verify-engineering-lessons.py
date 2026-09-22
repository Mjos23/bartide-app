from pathlib import Path
import json
import os
import subprocess
import shutil

ROOT = Path(__file__).resolve().parents[1]
RUN = ROOT / '.tools/engineering-lessons/verification'
RUN.mkdir(parents=True, exist_ok=True)
SDK = ROOT / '.tools/dotnet-10.0.401/dotnet.exe'
if not SDK.is_file():
    SDK = Path(shutil.which('dotnet') or 'dotnet')
env = os.environ.copy()
env.update(DOTNET_ROOT=str(SDK.parent), DOTNET_CLI_HOME=str(RUN / 'dotnet-home'),
           DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
           NUGET_PACKAGES=str(RUN / 'packages'), DOTNET_NOLOGO='1')
(RUN / 'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>\n', encoding='utf-8')
project = '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'''
mutations = {
    'money': ('MidpointRounding.AwayFromZero', 'MidpointRounding.ToEven', 'Half a cent rounds away from zero.'),
    'validation': ('quantity < 1 || quantity > 20', 'quantity < 0 || quantity > 20', 'Zero is rejected.'),
    'records': ('original with { PriceCents = 950 }', 'original with { PriceCents = 900 }', 'The copied price is 950 cents.'),
    'tenants': ('o.TenantId == tenantId && o.Id == orderId', 'o.Id == orderId', 'Harbor receives its own order.'),
    'retries': ('existing.Body != body', 'existing.Body.ItemId != body.ItemId', 'Changed details with the same key must be rejected.')
}

def run(name, args, cwd):
    result = subprocess.run([str(SDK), *args], cwd=cwd, env=env, capture_output=True,
                            text=True, encoding='utf-8', errors='replace', timeout=180,
                            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    (RUN / (name + '.log')).write_text(result.stdout + result.stderr, encoding='utf-8')
    return result

document = json.loads((ROOT / 'TideCasa.Blazor/wwwroot/engineering/csharp-lessons.json').read_text(encoding='utf-8'))
required = {'id', 'title', 'minutes', 'goal', 'explanation', 'steps', 'notebook', 'source', 'expected', 'challenge', 'sourcePaths'}
assert [item['id'] for item in document['lessons']] == list(mutations)
results = []
for item in document['lessons']:
    assert set(item) == required, item['id']
    assert all((ROOT / path).is_file() for path in item['sourcePaths'])
    work = RUN / item['id']
    work.mkdir(exist_ok=True)
    (work / 'Lesson.csproj').write_text(project, encoding='utf-8')
    source = work / 'Program.cs'
    source.write_text(item['source'], encoding='utf-8')
    restore = run(item['id'] + '-restore', ['restore', '--configfile', str(RUN / 'NuGet.Config'), '--nologo'], work)
    assert restore.returncode == 0, (item['id'], 'restore', restore.stdout, restore.stderr)
    baseline = run(item['id'] + '-pass', ['run', '--no-restore', '--disable-build-servers', '-p:UseSharedCompilation=false'], work)
    assert baseline.returncode == 0, (item['id'], 'run', baseline.stdout, baseline.stderr)
    assert baseline.stdout.strip() == item['expected'], (item['id'], 'expected', baseline.stdout)
    original, replacement, expected_failure = mutations[item['id']]
    assert item['source'].count(original) == 1
    source.write_text(item['source'].replace(original, replacement), encoding='utf-8')
    broken = run(item['id'] + '-intentional-bug', ['run', '--no-restore', '--disable-build-servers', '-p:UseSharedCompilation=false'], work)
    assert broken.returncode != 0, (item['id'], 'mutation passed unexpectedly')
    assert 'FAIL: ' + expected_failure in broken.stderr, (item['id'], 'wrong failure', broken.stderr)
    source.write_text(item['source'], encoding='utf-8')
    restored = run(item['id'] + '-restored', ['run', '--no-restore', '--disable-build-servers', '-p:UseSharedCompilation=false'], work)
    assert restored.returncode == 0 and restored.stdout.strip() == item['expected']
    results.append({'lesson': item['id'], 'lines': len(item['source'].splitlines()), 'restoreExit': restore.returncode,
                    'runExit': baseline.returncode, 'expectedOutputMatched': True,
                    'bugExit': broken.returncode, 'failureMessage': expected_failure, 'restoredExit': restored.returncode})
    print(json.dumps(results[-1]), flush=True)
(RUN / 'results.json').write_text(json.dumps({'sdk': subprocess.check_output([str(SDK), '--version'], text=True).strip(), 'targetFramework': 'net10.0',
    'packageSources': [], 'results': results}, indent=2) + '\n', encoding='utf-8')
print('PASS: all five original lessons, all five deliberate bugs, and all five restored lessons')
