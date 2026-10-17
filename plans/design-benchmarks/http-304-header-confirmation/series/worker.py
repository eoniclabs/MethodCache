"""Three independent, alternating, full-path BDN launches for each frozen revision.

Each launch gets fresh native backends. Results are evidence, never implicit acceptance.
No credentials or environment variables are written to the manifest.
"""
from pathlib import Path
import datetime, hashlib, json, subprocess, sys, time
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from process_activity import ActivityMonitor

OUT = Path(__file__).resolve().parent/'results'
HARNESS = Path(__file__).resolve().parent/'harness'
RUNNER = '/tmp/methodcache-http-native-runner/bin/Release/net10.0/Runner.dll'
REVISIONS = ['base','pr1','pr2','pr3','pr4','pr5','prefix','final']
GROUPS = {'revalidated': {'filters': ['*HttpResponsePathBenchmarks.Revalidated*'], 'reports':1,'cases':4}}

MONITOR = ActivityMonitor()

def utc(): return datetime.datetime.now(datetime.timezone.utc).isoformat()

def frozen(name):
    source = json.loads((HARNESS/name/'source-manifest.json').read_text())
    head = subprocess.check_output(['git','rev-parse','HEAD'],cwd=source['runtime_root'],text=True).strip()
    if head != source['runtime_sha']: raise SystemExit('Harness does not match current source: '+name)
    if hashlib.sha256((HARNESS/name/'Program.cs').read_bytes()).hexdigest()!=source['harness_program_sha256']:raise SystemExit('Harness program changed')
    for path,expected in source['source_sha256'].items():
        if hashlib.sha256((HARNESS/name/Path(path).name).read_bytes()).hexdigest()!=expected:raise SystemExit('Workload bytes changed')
    dirty = subprocess.check_output(['git','diff','--name-only','HEAD','--','src','benchmarks'],cwd=source['runtime_root'],text=True)
    if dirty.strip(): raise SystemExit('Source changed since compilation: '+name)
    return source

def activity(owned_pid=None):
    snapshot = subprocess.check_output(['ps','-Ao','pid=,ppid=,stat=,pcpu=,time=,etime=,command='],text=True)
    return MONITOR.observe(snapshot, owned_pid)

def idle():
    # Wait for a minute with no observed build/test activity. Never stop another task.
    quiet_since=time.monotonic();last_notice=0
    while time.monotonic()-quiet_since<60:
        if activity():
            quiet_since=time.monotonic()
            if quiet_since-last_notice>60:
                print('Deferring timing while external build/test work is active.',utc(),flush=True);last_notice=quiet_since
        time.sleep(2)

if len(sys.argv)>1 and sys.argv[1]=='worker':
    round_number,name,group=sys.argv[2:5]
    source=frozen(name)
    # The local child starts immediately after the parent's quiet minute. Native startup needs its own settling interval.
    idle()
    job=GROUPS[group]
    target=OUT/f'round-{round_number}-{name}-{group}'
    if target.exists(): raise SystemExit('Preserve the previous attempt before retrying: '+str(target))
    command=['dotnet',str(HARNESS/name/'bin/Release/net10.0/DesignValidation.dll'),
             '--filter',*job['filters'],'--warmupCount','16','--iterationCount','12',
             '--iterationTime','500','--launchCount','1','--exporters','json',
             '--stopOnFirstError','--artifacts',str(target)]
    with (OUT/f'round-{round_number}-{name}-{group}.log').open('w') as log:
        child=subprocess.Popen(command,cwd=HARNESS/name,stdout=log,stderr=subprocess.STDOUT)
        interference=[]
        while child.poll() is None:
            active=activity(child.pid)
            if active:interference.append({'at':utc(),'processes':active})
            time.sleep(2)
    (OUT/f'round-{round_number}-{name}-{group}-interference.json').write_text(json.dumps(interference,indent=2))
    if child.returncode: raise SystemExit(child.returncode)
    if interference:raise SystemExit('External build/test activity overlapped timing; this launch is not admissible evidence.')
    reports=list((target/'results').glob('*-report-full*.json'))
    if len(reports)!=job['reports']: raise SystemExit('Unexpected workload report count: '+str(len(reports)))
    count=0
    cases=set()
    for report in reports:
        data=json.loads(report.read_text())
        for case in data['Benchmarks']:
            key=(case['Type'],case['Method'],case['Parameters'])
            if key in cases:raise SystemExit('Duplicate measured case')
            cases.add(key)
            actual=[m for m in case['Measurements'] if m['IterationMode']=='Workload' and m['IterationStage']=='Actual']
            if len(actual)!=12:raise SystemExit('Missing actual pilot iterations')
            count+=1
            if not case.get('Statistics') or not case.get('Measurements'):
                raise SystemExit('Benchmark failed or has no measured samples: '+case.get('FullName','?'))
    expected={('HttpResponsePathBenchmarks','Revalidated',f'Listening={a}&AfterInvalidation={b}') for a in ('False','True') for b in ('False','True')}
    if cases!=expected:raise SystemExit('Substituted HTTP cases: '+repr(cases))
    if count!=job['cases']: raise SystemExit('Unexpected matrix size: '+str(count))
    frozen(name)
    print('Verified complete measured cases:',count,flush=True)
    raise SystemExit(0)
