"""Three independent, alternating, full-path BDN launches for each frozen revision.

Each launch gets fresh native backends. Results are evidence, never implicit acceptance.
No credentials or environment variables are written to the manifest.
"""
from pathlib import Path
import datetime, json, subprocess, sys, time
from process_activity import ActivityMonitor

OUT = Path('/tmp/methodcache-design-series-timing')
HARNESS = Path('/tmp/methodcache-design-harness')
RUNNER = '/tmp/methodcache-http-native-runner/bin/Release/net10.0/Runner.dll'
REVISIONS = ['base','pr1','pr2','pr3','pr4','pr5','prefix','final']
GROUPS = {
    'local': {'filters': ['*GeneratedContractBenchmarks*', '*HttpResponsePathBenchmarks.FreshHit*',
                          '*HttpResponsePathBenchmarks.VariantHit*', '*HttpResponsePathBenchmarks.Revalidated*',
                          '*HttpColdMissBenchmarks*',
                          '*DesignMemoryWorkloadBenchmarks*', '*DesignHttpWorkloadBenchmarks*Local*'],
              'reports': 5, 'cases': 46},
    'native': {'filters': ['*DesignL2WorkloadBenchmarks*', '*DesignHttpWorkloadBenchmarks*Redis*',
                           '*DesignHttpWorkloadBenchmarks*SqlServer*'],
               'reports': 2, 'cases': 16},
}
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
    if group == 'native' or activity(): idle()
    job=GROUPS[group]
    target=OUT/f'round-{round_number}-{name}-{group}'
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
    for report in reports:
        data=json.loads(report.read_text())
        for case in data['Benchmarks']:
            count+=1
            if not case.get('Statistics') or not case.get('Measurements'):
                raise SystemExit('Benchmark failed or has no measured samples: '+case.get('FullName','?'))
    if count!=job['cases']: raise SystemExit('Unexpected matrix size: '+str(count))
    frozen(name)
    print('Verified complete measured cases:',count,flush=True)
    raise SystemExit(0)

OUT.mkdir(exist_ok=True)
subprocess.run([sys.executable, '-m', 'unittest', 'discover', '-s', str(Path(__file__).resolve().parent),
                '-p', 'test_process_activity.py'], check=True)
sources={name:frozen(name) for name in REVISIONS}
checksums=[source['source_sha256'] for source in sources.values()]
if any(value!=checksums[0] for value in checksums): raise SystemExit('Workload sources differ across revisions.')
manifest_path=OUT/'metadata.json'
if manifest_path.exists():
    manifest=json.loads(manifest_path.read_text())
    if manifest['sources']!=sources: raise SystemExit('Existing evidence belongs to different frozen sources.')
else:
    manifest={'purpose':'Section 10: all adjacent PR comparisons and cumulative f43854d comparison',
              'started':utc(),'sources':sources,'sdk':subprocess.check_output(['dotnet','--info'],text=True),
              'cpu':subprocess.check_output(['sysctl','-n','machdep.cpu.brand_string'],text=True).strip(),
              'gc':'Concurrent workstation, .NET 10, same benchmark job on every revision',
              'backend':'Local groups run without test containers. Native groups get fresh Redis 7 and SQL Server 2022 containers, same local images.',
              'order':'descending / ascending / descending; three independent launches per revision',
              'note':'Feature additions (RoundTrip before PR4, KeepCookies before HTTP) are classified separately, not comparable old behavior.',
              'runs':[]}
    manifest_path.write_text(json.dumps(manifest,indent=2))
for round_number,order in enumerate([list(reversed(REVISIONS)),REVISIONS,list(reversed(REVISIONS))],1):
    for name in order:
        for group in GROUPS:
            if any(run['round']==round_number and run['revision']==name and run['group']==group and run['exit']==0 for run in manifest['runs']): continue
            frozen(name);idle()
            print('starting',round_number,name,group,sources[name]['runtime_sha'],utc(),flush=True)
            started=utc()
            child=['python3',str(Path(__file__).resolve()),'worker',str(round_number),name,group]
            command=['dotnet',RUNNER,sources[name]['runtime_root'],*child] if group=='native' else child
            with (OUT/f'round-{round_number}-{name}-{group}-driver.log').open('w') as log:
                result=subprocess.run(command,stdout=log,stderr=subprocess.STDOUT)
            manifest['runs'].append({'round':round_number,'revision':name,'group':group,'started':started,'finished':utc(),'exit':result.returncode})
            manifest_path.write_text(json.dumps(manifest,indent=2))
            print('finished',round_number,name,group,result.returncode,utc(),flush=True)
            if result.returncode: raise SystemExit(result.returncode)
print('Completed all 48 family launches (24 complete revision rounds). Statistical acceptance still requires review.',flush=True)
