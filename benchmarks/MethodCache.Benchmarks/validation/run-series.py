"""Three independent, alternating, full-path BDN launches for each frozen revision.

Each launch gets fresh native backends. Results are evidence, never implicit acceptance.
No credentials or environment variables are written to the manifest.
"""
from pathlib import Path
import datetime, json, subprocess, sys, time

OUT = Path('/tmp/methodcache-design-series-timing')
HARNESS = Path('/tmp/methodcache-design-harness')
RUNNER = '/tmp/methodcache-http-native-runner/bin/Release/net10.0/Runner.dll'
REVISIONS = ['base','pr1','pr2','pr3','pr4','pr5','prefix','final']

def utc(): return datetime.datetime.now(datetime.timezone.utc).isoformat()

def frozen(name):
    source = json.loads((HARNESS/name/'source-manifest.json').read_text())
    head = subprocess.check_output(['git','rev-parse','HEAD'],cwd=source['runtime_root'],text=True).strip()
    if head != source['runtime_sha']: raise SystemExit('Harness does not match current source: '+name)
    dirty = subprocess.check_output(['git','diff','--name-only','HEAD','--','src','benchmarks'],cwd=source['runtime_root'],text=True)
    if dirty.strip(): raise SystemExit('Source changed since compilation: '+name)
    return source

def activity(owned_pid=None):
    processes=[]
    for row in subprocess.check_output(['ps','-Ao','pid=,ppid=,command='],text=True).splitlines():
        pieces=row.strip().split(None,2)
        if len(pieces)==3: processes.append((int(pieces[0]),int(pieces[1]),pieces[2]))
    owned={owned_pid} if owned_pid else set()
    while True:
        children={pid for pid,parent,_ in processes if parent in owned}
        if children.issubset(owned):break
        owned.update(children)
    return [{'pid':pid,'kind':'testhost' if 'testhost.dll' in command else 'build/test/publish'}
            for pid,_,command in processes if pid not in owned and
            ('testhost.dll' in command or 'dotnet build ' in command or 'dotnet test ' in command or 'dotnet publish ' in command)]

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
    round_number,name=sys.argv[2:4]
    source=frozen(name); idle()
    target=OUT/f'round-{round_number}-{name}'
    command=['dotnet',str(HARNESS/name/'bin/Release/net10.0/DesignValidation.dll'),
             '--filter','*','--warmupCount','16','--iterationCount','12',
             '--iterationTime','500','--launchCount','1','--exporters','json',
             '--stopOnFirstError','--artifacts',str(target)]
    with (OUT/f'round-{round_number}-{name}.log').open('w') as log:
        child=subprocess.Popen(command,cwd=HARNESS/name,stdout=log,stderr=subprocess.STDOUT)
        interference=[]
        while child.poll() is None:
            active=activity(child.pid)
            if active:interference.append({'at':utc(),'processes':active})
            time.sleep(2)
    (OUT/f'round-{round_number}-{name}-interference.json').write_text(json.dumps(interference,indent=2))
    if child.returncode: raise SystemExit(child.returncode)
    if interference:raise SystemExit('External build/test activity overlapped timing; this launch is not admissible evidence.')
    reports=list((target/'results').glob('*-report-full*.json'))
    if len(reports)!=5: raise SystemExit('Expected five complete workload reports, found '+str(len(reports)))
    count=0
    for report in reports:
        data=json.loads(report.read_text())
        for case in data['Benchmarks']:
            count+=1
            if not case.get('Statistics') or not case.get('Measurements'):
                raise SystemExit('Benchmark failed or has no measured samples: '+case.get('FullName','?'))
    if count!=62: raise SystemExit('Unexpected matrix size: '+str(count))
    frozen(name)
    print('Verified complete measured cases:',count,flush=True)
    raise SystemExit(0)

OUT.mkdir(exist_ok=True)
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
              'backend':'Fresh Redis 7 alpine and SQL Server 2022 containers per revision launch; same local container images',
              'order':'descending / ascending / descending; three independent launches per revision',
              'note':'Feature additions (RoundTrip before PR4, KeepCookies before HTTP) are classified separately, not comparable old behavior.',
              'runs':[]}
    manifest_path.write_text(json.dumps(manifest,indent=2))
for round_number,order in enumerate([list(reversed(REVISIONS)),REVISIONS,list(reversed(REVISIONS))],1):
    for name in order:
        if any(run['round']==round_number and run['revision']==name and run['exit']==0 for run in manifest['runs']): continue
        frozen(name);idle()
        print('starting',round_number,name,sources[name]['runtime_sha'],utc(),flush=True)
        started=utc()
        with (OUT/f'round-{round_number}-{name}-driver.log').open('w') as log:
            result=subprocess.run(['dotnet',RUNNER,sources[name]['runtime_root'],'python3',str(Path(__file__).resolve()),'worker',str(round_number),name],stdout=log,stderr=subprocess.STDOUT)
        manifest['runs'].append({'round':round_number,'revision':name,'started':started,'finished':utc(),'exit':result.returncode})
        manifest_path.write_text(json.dumps(manifest,indent=2))
        print('finished',round_number,name,result.returncode,utc(),flush=True)
        if result.returncode: raise SystemExit(result.returncode)
print('Completed all 24 full-path launches. Statistical acceptance still requires review.',flush=True)
