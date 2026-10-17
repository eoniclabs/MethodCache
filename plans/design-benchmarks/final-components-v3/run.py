"""One current-runtime component diagnostic; not a historical matrix or paired-time acceptance."""
from pathlib import Path
import datetime
import hashlib
import importlib.util
import json
import math
import os
import subprocess
import sys
import time

folder = Path(__file__).resolve().parent
root = folder.parent
inputs = json.loads((folder / 'inputs.json').read_text())
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
utc = lambda: datetime.datetime.now(datetime.timezone.utc).isoformat()

def call(args, **kwargs):
    return subprocess.run(list(map(str, args)), check=True, text=True, **kwargs)

def prepare():
    assert sha(folder / 'ComponentGuards.cs') == inputs['guard_sha256']
    assert sha(folder / 'process_activity.py') == inputs['activity_sha256']
    tree = folder / 'tree'
    call(['git', 'clone', '--no-checkout', '--config', 'gc.auto=0', '--config', 'maintenance.auto=false',
          root / 'final-paired-cost-v2/source.bundle', tree])
    call(['git', '-C', tree, 'checkout', '--detach', inputs['head']])
    for relative, expected in inputs['sources'].items():
        source = folder / 'sources' / relative
        assert sha(source) == expected
        target = tree / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(source.read_bytes())
    project = tree / 'benchmarks/MethodCache.ComponentProfile'
    project.mkdir()
    original = tree / 'benchmarks/MethodCache.CostProfile'
    for p in original.glob('*.cs'):
        (project / p.name).write_bytes(p.read_bytes())
    csproj = (original / 'MethodCache.CostProfile.csproj').read_text()
    csproj = csproj.replace('<OutputType>Exe</OutputType>', '<OutputType>Exe</OutputType><AssemblyName>MethodCache.Benchmarks</AssemblyName><DefineConstants>$(DefineConstants);METHODCACHE_PREFIX_HASH;METHODCACHE_SIPHASH;METHODCACHE_KEY_BUILDER_CONSTRUCTOR</DefineConstants>')
    links = ''.join(f'<Compile Include="../MethodCache.Benchmarks/Microbenchmarks/{name}.cs" Link="Harness/{name}.cs" />' for name in ['PrefixHitPathBenchmarks', 'KeyHashLookupBenchmarks', 'MemoryCacheLookupReferenceBenchmarks'])
    csproj = csproj.replace('</Project>', '<ItemGroup><PackageReference Include="Microsoft.Extensions.Caching.Memory" Version="9.0.9" />' + links + '</ItemGroup></Project>')
    (project / 'MethodCache.ComponentProfile.csproj').write_text(csproj)
    program = (project / 'Program.cs').read_text()
    start = program.index('    private static readonly Type[] Types =')
    end = program.index('    public static async Task<int> Main', start)
    program = program[:start] + '    private static readonly Type[] Types = [typeof(MethodCache.Benchmarks.Microbenchmarks.PrefixHitPathBenchmarks), typeof(MethodCache.Benchmarks.Microbenchmarks.KeyHashLookupBenchmarks), typeof(MethodCache.Benchmarks.Microbenchmarks.MemoryCacheLookupReferenceBenchmarks)];\n\n' + program[end:]
    marker = '    public static async Task<int> Main(string[] args)\n    {'
    assert marker in program
    program = program.replace(marker, marker + '\n        if (args.Contains("--verify-components")) { await global::MethodCache.CostProfile.ComponentGuards.RunAsync(); return 0; }', 1)
    (project / 'Program.cs').write_text(program)
    (project / 'ComponentGuards.cs').write_bytes((folder / 'ComponentGuards.cs').read_bytes())
    assert not subprocess.check_output(['git', '-C', str(tree), 'diff', '--name-only', inputs['head'], '--', 'src'], text=True).strip()
    return project

def worker():
    assert sha(Path(__file__)) == inputs['controller_sha256']
    project = prepare()
    uid, gid = str(os.getuid()), str(os.getgid())
    args = ['docker', 'run', '--rm', '--init', '--pid', 'host', '--cpuset-cpus', '0-15', '--user', uid + ':' + gid,
            '--label', 'methodcache-design-bench=' + inputs['label'], '--volume', str(root) + ':' + str(root),
            '--volume', str(root / 'nuget') + ':/nuget', '--volume', str(root / 'home') + ':/tmp/home',
            '--workdir', str(project), '--env', 'DOTNET_gcServer=0', '--env', 'DOTNET_gcConcurrent=1',
            inputs['image'], 'python3', '-B', '-u', str(Path(__file__)), 'inner']
    call(args)

def inner():
    project = folder / 'tree/benchmarks/MethodCache.ComponentProfile'
    out = folder / 'results'
    out.mkdir()
    call(['dotnet', 'build', project / 'MethodCache.ComponentProfile.csproj', '-c', 'Release', '-o', folder / 'bin',
          '-p:NBGV_GitEngine=Disabled', '-p:UseSharedCompilation=false', '-nodeReuse:false', '-nologo', '-v:q'])
    dll = folder / 'bin/MethodCache.Benchmarks.dll'
    with (out / 'setup-guards.log').open('x') as log:
        call(['dotnet', dll, '--verify-components'], stdout=log, stderr=subprocess.STDOUT)
    inventory = subprocess.check_output(['dotnet', str(dll), '--list'], text=True).splitlines()
    cases = [n for n in inventory if n.startswith(('KeyHashLookupBenchmarks.', 'MemoryCacheLookupReferenceBenchmarks.', 'PrefixHitPathBenchmarks.KeyBuild('))]
    assert len(cases) == len(set(cases)) == 30
    assert len([c for c in cases if c.startswith('KeyHashLookup')]) == 18
    assert len([c for c in cases if c.startswith('MemoryCache')]) == 4
    (out / 'inventory.json').write_text(json.dumps(cases, indent=2) + '\n')
    files = [{'path': str(p.relative_to(folder)), 'sha256': sha(p)} for p in sorted((folder / 'bin').rglob('*')) if p.is_file()]
    (out / 'compiled-checksums.json').write_text(json.dumps(files, indent=2) + '\n')
    call(['dotnet', dll, '--out', out / 'cost.jsonl'])
    cost = [json.loads(l) for l in (out / 'cost.jsonl').read_text().splitlines()]
    assert len(cost) == 54 and not any(c.get('error') for c in cost)
    spec = importlib.util.spec_from_file_location('activity', folder / 'process_activity.py')
    activity = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(activity)
    monitor = activity.ActivityMonitor()
    observations = []
    for _ in range(60):
        raw = subprocess.check_output(['ps', '-eo', 'pid=,ppid=,stat=,pcpu=,time=,etime=,args='], text=True)
        assert not monitor.observe(raw, owned_pid=os.getpid()), 'Preserve competing-work failure'
        time.sleep(1)
    for index, case in enumerate(cases):
        output = out / f'case-{index:02d}.jsonl'
        started = utc()
        with (out / f'case-{index:02d}.log').open('x') as log:
            child = subprocess.Popen(['dotnet', str(dll), case, '--exact', '--time', '--out', str(output)], stdout=log, stderr=subprocess.STDOUT)
            while child.poll() is None:
                raw = subprocess.check_output(['ps', '-eo', 'pid=,ppid=,stat=,pcpu=,time=,etime=,args='], text=True)
                external = monitor.observe(raw, owned_pid=os.getpid())
                affinity = sorted(os.sched_getaffinity(child.pid)) if Path(f'/proc/{child.pid}').exists() else []
                observations.append({'utc': utc(), 'external': external, 'pid': child.pid, 'affinity': affinity})
                assert not external and set(affinity).issubset(set(range(16)))
                time.sleep(.5)
            assert child.returncode == 0
        record = json.loads(output.read_text())
        assert record['case'] == case and record['actualIterations'] == len(record['actualMeasurements']) == 12
        assert record['runtime'] == '.NET 10.0.12' and not record['serverGC'] and record['gcLatencyMode'] == 'Interactive'
        assert math.isfinite(record['ns']) and record['ns'] > 0
        with (out / 'launches.jsonl').open('a') as log:
            log.write(json.dumps({'case': case, 'output': output.name, 'sha256': sha(output), 'started_utc': started, 'finished_utc': utc()}) + '\n')
        print('Finished component diagnostic', index + 1, 'of30', utc(), flush=True)
    (out / 'observations.json').write_text(json.dumps(observations) + '\n')
    (out / 'exit').write_text('0\n')

assert (root / 'OWNER').read_text().strip() == 'methodcache-design-20261015-01a13a93'
if sys.argv[1] == 'inner':
    inner()
elif sys.argv[1] == 'worker':
    worker()
else:
    assert not (folder / 'started.json').exists()
    (folder / 'started.json').write_text(json.dumps({'utc': utc(), 'inputs_sha256': sha(folder / 'inputs.json')}) + '\n')
    while True:
        with (folder / 'worker.log').open('a') as log:
            result = subprocess.run(['flock', '--exclusive', '--nonblock', '--conflict-exit-code', '75', '/srv/skribera-ci/ci-build-capacity.lock',
                                     'python3', str(root / 'leased-batch.py'), inputs['label'], 'python3', '-B', '-u', str(Path(__file__)), 'worker'],
                                    stdout=log, stderr=subprocess.STDOUT)
        if result.returncode != 75:
            break
        time.sleep(2)
    (folder / 'exit').write_text(str(result.returncode) + '\n')
    print('Current component diagnostic terminal; not a paired regression verdict', result.returncode, utc(), flush=True)
