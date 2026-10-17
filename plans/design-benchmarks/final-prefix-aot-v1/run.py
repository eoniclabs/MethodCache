"""Verify the explicit F05 native execution cases under the existing bounded CI lease."""
from pathlib import Path
import datetime
import hashlib
import json
import os
import subprocess
import sys
import time

folder = Path(__file__).resolve().parent
root = folder.parent
inputs = json.loads((folder / 'inputs.json').read_text())
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
utc = lambda: datetime.datetime.now(datetime.timezone.utc).isoformat()

def worker():
    assert sha(Path(__file__)) == inputs['controller_sha256']
    tree = folder / 'tree'
    subprocess.run(['git', 'clone', '--no-checkout', '--config', 'gc.auto=0', '--config',
                    'maintenance.auto=false', str(root / 'final-paired-cost-v2/source.bundle'), str(tree)], check=True)
    subprocess.run(['git', '-C', str(tree), 'checkout', '--detach', inputs['head']], check=True)
    for relative, expected in inputs['sources'].items():
        source = folder / 'sources' / relative
        assert sha(source) == expected
        target = tree / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(source.read_bytes())
    assert not subprocess.check_output(['git', '-C', str(tree), 'diff', '--name-only', inputs['head'], '--', 'src'], text=True).strip()
    out = folder / 'results'
    out.mkdir()
    project = 'tests/MethodCache.AotSmokeTest/MethodCache.AotSmokeTest.csproj'
    common = ['docker', 'run', '--rm', '--init', '--cpuset-cpus', '0-15', '--user', f'{os.getuid()}:{os.getgid()}',
              '--label', 'methodcache-design-bench=' + inputs['label'],
              '--volume', str(root) + ':' + str(root), '--volume', str(root / 'nuget') + ':/nuget',
              '--volume', str(root / 'home') + ':/tmp/home', '--workdir', str(tree), inputs['image']]
    phases = [
        ('sdk-info', ['dotnet', '--info']),
        ('jit', ['dotnet', 'run', '--project', project, '-c', 'Release', '-p:NBGV_GitEngine=Disabled']),
        ('format', ['dotnet', 'format', project, '--no-restore', '--verify-no-changes', '--include', *inputs['sources']]),
        ('publish', ['dotnet', 'publish', project, '-c', 'Release', '--runtime', 'linux-x64', '-o', str(out / 'native'), '-p:NBGV_GitEngine=Disabled']),
        ('native', [str(out / 'native/MethodCache.AotSmokeTest')]),
    ]
    for name, command in phases:
        with (out / (name + '.log')).open('x') as log:
            result = subprocess.run(common + command, stdout=log, stderr=subprocess.STDOUT)
        (out / (name + '.exit')).write_text(str(result.returncode) + '\n')
        print('Finished explicit prefix execution check', name, result.returncode, utc(), flush=True)
        assert result.returncode == 0, (name, result.returncode)
    (out / 'receipt.json').write_text(json.dumps({**inputs, 'completed_utc': utc(), 'production_diff_empty': True}, indent=2) + '\n')

if sys.argv[1] == 'worker':
    worker()
else:
    assert (root / 'OWNER').read_text().strip() == 'methodcache-design-20261015-01a13a93'
    assert sha(root / 'leased-batch.py') == '4a83e0b505eaef6d91599479c8fa6c78ddc2958e698882740cca7ecad57a6830'
    assert not (folder / 'started.json').exists()
    (folder / 'started.json').write_text(json.dumps({'utc': utc(), 'inputs_sha256': sha(folder / 'inputs.json')}) + '\n')
    print('Waiting for existing exclusive CI capacity', utc(), flush=True)
    while True:
        with (folder / 'worker.log').open('a') as log:
            result = subprocess.run(['flock', '--exclusive', '--nonblock', '--conflict-exit-code', '75',
                                     '/srv/skribera-ci/ci-build-capacity.lock', 'python3', str(root / 'leased-batch.py'),
                                     inputs['label'], 'python3', '-B', '-u', str(Path(__file__)), 'worker'],
                                    stdout=log, stderr=subprocess.STDOUT)
        if result.returncode != 75:
            break
        time.sleep(2)
    (folder / 'exit').write_text(str(result.returncode) + '\n')
    print('Prefix JIT/native verification terminal; inspect every phase receipt', result.returncode, utc(), flush=True)
