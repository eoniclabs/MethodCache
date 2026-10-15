"""Three independent, alternating, full-path BDN launches for each frozen revision.

Each launch gets fresh native backends. Results are evidence, never implicit acceptance.
No credentials or environment variables are written to the manifest.
"""
from pathlib import Path
import datetime, json, subprocess, sys, time, os, re
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from process_activity import ActivityMonitor, parse_snapshot
from bdn_affinity import owned_dotnet_affinities

OUT = Path(__file__).resolve().parent/'results'
HARNESS = Path(__file__).resolve().parent/'harness'
RUNNER = '/tmp/methodcache-http-native-runner/bin/Release/net10.0/Runner.dll'
REVISIONS = ['base','pr1','pr2','pr3','pr4','pr5','prefix','final']
GROUPS = {'precision': {'filters': ['*GeneratedContractBenchmarks.SyncHit*','*GeneratedContractBenchmarks.GenericHit*','*GeneratedContractBenchmarks.TaskHit*','*GeneratedContractBenchmarks.ValueTaskHit*'], 'reports':1,'cases':4}}

MONITOR = ActivityMonitor()

def utc(): return datetime.datetime.now(datetime.timezone.utc).isoformat()

def frozen(name):
    source = json.loads((HARNESS/name/'source-manifest.json').read_text())
    head = subprocess.check_output(['git','rev-parse','HEAD'],cwd=source['runtime_root'],text=True).strip()
    if head != source['runtime_sha']: raise SystemExit('Harness does not match current source: '+name)
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
    command=['taskset','--cpu-list','2','dotnet',str(HARNESS/name/'bin/Release/net10.0/DesignValidation.dll'),
             '--filter',*job['filters'],'--warmupCount','16','--iterationCount','12',
             '--iterationTime','500','--launchCount','1','--exporters','json',
             '--stopOnFirstError','--artifacts',str(target)]
    with (OUT/f'round-{round_number}-{name}-{group}.log').open('w') as log:
        child=subprocess.Popen(command,cwd=HARNESS/name,stdout=log,stderr=subprocess.STDOUT)
        interference=[]
        observed_affinity={}
        root_pid=child.pid
        while child.poll() is None:
            snapshot=subprocess.check_output(['ps','-Ao','pid=,ppid=,stat=,pcpu=,time=,etime=,command='],text=True)
            active=MONITOR.observe(snapshot,child.pid)
            for pid,cpus in owned_dotnet_affinities(parse_snapshot(snapshot),child.pid,os.sched_getaffinity).items():
                row=observed_affinity.setdefault(str(pid),{'allowed_cpus':cpus,'observations':0,'mismatch':False})
                row['observations']+=1
                row['mismatch']=row['mismatch'] or cpus!=[2]
            if active:interference.append({'at':utc(),'processes':active})
            time.sleep(2)
    (OUT/f'round-{round_number}-{name}-{group}-interference.json').write_text(json.dumps(interference,indent=2))
    output_log=(OUT/f'round-{round_number}-{name}-{group}.log').read_text()
    benchmark_pids=re.findall(r'// Benchmark Process (\d+) has exited with code 0\.',output_log)
    affinity_evidence={'root_pid':root_pid,'benchmark_pids':benchmark_pids,'processes':observed_affinity}
    (OUT/f'round-{round_number}-{name}-{group}-affinity.json').write_text(json.dumps(affinity_evidence,indent=2))
    if child.returncode: raise SystemExit(child.returncode)
    if 'Failed to set up processor affinity' in output_log:raise SystemExit('BDN attempted a failed affinity change')
    required={str(root_pid),*benchmark_pids}
    if len(set(benchmark_pids))!=job['cases'] or not required.issubset(observed_affinity):
        raise SystemExit('Missing actual OS affinity samples for the host or benchmark child processes')
    if any(row['allowed_cpus']!=[2] or row['mismatch'] for row in observed_affinity.values()):
        raise SystemExit('An owned .NET process was observed outside CPU2')
    if interference:raise SystemExit('External build/test activity overlapped timing; this launch is not admissible evidence.')
    reports=list((target/'results').glob('*-report-full*.json'))
    if len(reports)!=job['reports']: raise SystemExit('Unexpected workload report count: '+str(len(reports)))
    count=0
    for report in reports:
        data=json.loads(report.read_text())
        for case in data['Benchmarks']:
            actual=[m for m in case['Measurements'] if m['IterationMode']=='Workload' and m['IterationStage']=='Actual']
            if len(actual)!=12:raise SystemExit('Missing actual pilot iterations')
            count+=1
            if not case.get('Statistics') or not case.get('Measurements'):
                raise SystemExit('Benchmark failed or has no measured samples: '+case.get('FullName','?'))
    if count!=job['cases']: raise SystemExit('Unexpected matrix size: '+str(count))
    frozen(name)
    print('Verified complete measured cases:',count,flush=True)
    raise SystemExit(0)
