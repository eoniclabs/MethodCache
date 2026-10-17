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
    original = root / 'eviction-hash-experiment-v1'
    original_prepared = json.loads((original / 'prepared.json').read_text())
    tree = folder / 'tree'
    subprocess.run(['git', 'clone', '--no-checkout', '--config', 'gc.auto=0', '--config', 'maintenance.auto=false',
                    str(root / 'final-paired-cost-v2/source.bundle'), str(tree)], check=True)
    subprocess.run(['git', '-C', str(tree), 'checkout', '--detach', inputs['head']], check=True)
    assert sha(folder / 'runtime.patch') == inputs['runtime_patch_sha256']
    subprocess.run(['git', '-C', str(tree), 'apply', str(folder / 'runtime.patch')], check=True)
    for relative, expected in original_prepared['source_hashes']['head'].items():
        source = original / 'tree' / relative
        assert sha(source) == expected
        target = tree / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(source.read_bytes())
    (folder / 'bin/base').mkdir(parents=True)
    (folder / 'bin/base/main').symlink_to(original / 'bin/head/main', target_is_directory=True)
    command = ['docker', 'run', '--rm', '--init', '--cpuset-cpus', '0-15', '--user', f'{os.getuid()}:{os.getgid()}',
               '--label', 'methodcache-design-bench=' + inputs['label'], '--volume', str(root) + ':' + str(root),
               '--volume', str(root / 'nuget') + ':/nuget', '--volume', str(root / 'home') + ':/tmp/home',
               '--workdir', str(tree), inputs['image']]
    results = folder / 'preparation'
    results.mkdir()
    phases = [
        ('core-tests', ['dotnet', 'test', 'tests/MethodCache.Core.Tests/MethodCache.Core.Tests.csproj', '-c', 'Release', '-f', 'net10.0', '-p:NBGV_GitEngine=Disabled']),
        ('build', ['dotnet', 'build', 'benchmarks/MethodCache.CostProfile/MethodCache.CostProfile.csproj', '-c', 'Release', '-o', str(folder / 'bin/head/main'), '-p:NBGV_GitEngine=Disabled', '-nologo', '-v:q']),
    ]
    for name, args in phases:
        with (results / (name + '.log')).open('x') as log:
            result = subprocess.run(command + args, stdout=log, stderr=subprocess.STDOUT)
        (results / (name + '.exit')).write_text(str(result.returncode) + '\n')
        assert result.returncode == 0, (name, result.returncode)
    binaries = {}
    for side in ['base', 'head']:
        binaries[side + '/main'] = {str(p.relative_to(folder)): sha(p) for p in sorted((folder / 'bin' / side / 'main').iterdir()) if p.is_file()}
    sources = {side: original_prepared['source_hashes']['head'] for side in ['base', 'head']}
    prepared = {'inputs_sha256': sha(folder / 'inputs.json'), 'prepared_utc': utc(),
                'revisions': {'base': inputs['base'], 'head': inputs['head']},
                'head_patch_sha256': inputs['runtime_patch_sha256'], 'source_hashes': sources, 'binaries': binaries,
                'groups': [[{'family': 'main', 'case': c} for c in inputs['cases']]],
                'sdk': original_prepared['sdk'], 'cpu': original_prepared['cpu'],
                'protocol': 'Isolated cached immutable snapshot inputs versus existing indirections; both include node-hash reuse. Three generated GUID return shapes/tenfreshpairs. Binding,retirement,hashing,telemetryandcustommanagerbehaviorpreserved;noAPIchange.'}
    (folder / 'prepared.json').write_text(json.dumps(prepared, indent=2) + '\n')
    (results / 'exit').write_text('0\n')
    print('Candidate Core tests and build pass; fresh timing dispatch still requires independent source/binary verification', utc(), flush=True)

if sys.argv[1] == 'worker':
    worker()
else:
    assert (root / 'OWNER').read_text().strip() == 'methodcache-design-20261015-01a13a93'
    assert not (folder / 'prepare-started.json').exists()
    (folder / 'prepare-started.json').write_text(json.dumps({'utc': utc(), 'inputs_sha256': sha(folder / 'inputs.json')}) + '\n')
    while True:
        with (folder / 'prepare-worker.log').open('a') as log:
            result = subprocess.run(['flock', '--exclusive', '--nonblock', '--conflict-exit-code', '75',
                                     '/srv/skribera-ci/ci-build-capacity.lock', 'python3', str(root / 'leased-batch.py'),
                                     inputs['label'], 'python3', '-B', '-u', str(Path(__file__)), 'worker'], stdout=log, stderr=subprocess.STDOUT)
        if result.returncode != 75:
            break
        time.sleep(2)
    (folder / 'prepare.exit').write_text(str(result.returncode) + '\n')
    print('Eviction hash reuse preparation terminal', result.returncode, utc(), flush=True)
