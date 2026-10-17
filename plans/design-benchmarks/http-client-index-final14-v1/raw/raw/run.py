"""Independent quiet-Mac confirmation; no pooling with noisy Nimbus evidence."""
from pathlib import Path
import datetime,hashlib,importlib.util,json,math,os,signal,subprocess,sys,time
folder=Path(__file__).resolve().parent
repo=Path('/Users/johan/.codex/worktrees/design-combined-gates-v1/MethodCache')
inputs=json.loads((folder/'inputs.json').read_text())
digest=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
utc=lambda:datetime.datetime.now(datetime.timezone.utc).isoformat()
lock=Path.home()/'.cache/methodcache-ci/coordinator.lock'
deadline=0
child=None

def capture(args,**kw):
    return subprocess.check_output(list(map(str,args)),text=True,**kw).strip()

def check_lock():
    assert (lock/'pid').read_text().strip()==str(os.getpid()),'Coordinator reservation lost'
    assert time.monotonic()<deadline,'Bounded local reservation expired'

def run(args,**kw):
    global child
    check_lock()
    child=subprocess.Popen(list(map(str,args)),text=True,start_new_session=True,**kw)
    try:
        code=child.wait(timeout=max(1,deadline-time.monotonic()-20))
        if code:raise subprocess.CalledProcessError(code,args)
    except BaseException:
        if child.poll() is None:
            os.killpg(child.pid,signal.SIGTERM)
            try:child.wait(timeout=10)
            except subprocess.TimeoutExpired:os.killpg(child.pid,signal.SIGKILL);child.wait()
        raise
    finally:child=None

def verify_inputs():
    assert digest(Path(__file__))==inputs['controller_sha256']
    for relative,expected in inputs['source_sha256'].items():
        assert digest(folder/'harness'/relative)==expected,relative
    assert digest(folder/'process_activity.py')==inputs['activity_sha256']
    for relative,expected in inputs['frozen_reviewers'].items():assert digest(folder/relative)==expected

def prepare():
    import prepare_host
    prepare_host.prepare(globals())

def snapshot(monitor):
    raw=capture(['ps','-axo','pid=,ppid=,stat=,pcpu=,time=,etime=,args='])
    external=monitor.observe(raw,owned_pid=os.getpid())
    processes=monitor_module.parse_snapshot(raw)
    owned={os.getpid()}
    while True:
        more={p.pid for p in processes if p.parent in owned}
        if more.issubset(owned):break
        owned.update(more)
    cpu=sum(p.cpu_percent or 0 for p in processes if p.pid not in owned)
    sample={'utc':utc(),'external':external,'other_process_cpu_percent':round(cpu,2)}
    if external:
        # Diagnostics only after admission already fails. Never retain full command arguments.
        import shlex
        identified={p.pid:p for p in processes}
        observed=[]
        requested=set()
        for record in external:
            pid=record['pid'];requested.add(pid)
            process=identified.get(pid)
            if process is not None:
                requested.add(process.parent)
                try:words=shlex.split(process.command)
                except ValueError:words=[]
                observed.append({'pid':pid,'parent':process.parent,'cpu_percent':process.cpu_percent,
                                 'xcodebuild_version_only':bool(words and Path(words[0]).name=='xcodebuild' and words[1:]==['-version'])})
        result=subprocess.run(['ps','-p',','.join(map(str,sorted(requested))),'-o','pid=,ppid=,comm='],capture_output=True,text=True)
        sample['external_identities']={'process_metadata':result.stdout.strip().splitlines(),'classification':observed,'ps_exit':result.returncode,
                                       'scope':'Executable and parent metadata only; full arguments never stored. Admission condition unchanged.'}
    return sample

def measure(number):
    global child
    verify_inputs()
    prepared=json.loads((folder/'prepared.json').read_text())
    assert prepared['inputs_sha256']==digest(folder/'inputs.json')
    row=next(r for r in inputs['comparisons'] if r['pr']==number)
    selected_cases=row.get('cases',inputs['cases'])
    destination=folder/f'pr-{number}';destination.mkdir()
    (destination/'intent.json').write_text(json.dumps({'utc':utc(),'inputs_sha256':digest(folder/'inputs.json'),'prepared_sha256':digest(folder/'prepared.json'),'comparison':row,'cases':selected_cases},indent=2)+'\n')
    monitor=monitor_module.ActivityMonitor();observations=[]
    env=dict(os.environ,DOTNET_gcServer='0',DOTNET_gcConcurrent='1')
    try:
        consecutive=0
        while consecutive<60:
            check_lock();sample=snapshot(monitor);sample['phase']='preflight';observations.append(sample)
            quiet=not sample['external'] and sample['other_process_cpu_percent']<=inputs['host_quiet_limit_other_process_cpu_percent']
            consecutive=consecutive+1 if quiet else 0
            time.sleep(1)
        (destination/'quiet-confirmed.json').write_text(json.dumps({'utc':utc(),'consecutive_quiet_samples':60,'sample_period_seconds':1,'other_process_cpu_limit':inputs['host_quiet_limit_other_process_cpu_percent'],'reservation_deadline_seconds':1470},indent=2)+'\n')
        for pair in range(1,inputs['pairs']+1):
            for family in ['main','int']:
                for index,case in enumerate(selected_cases[family]):
                    for side in (['base','head'] if pair%2 else ['head','base']):
                        check_lock();point=row[side]
                        for binary in prepared['binaries'][point+'/'+family]:
                            assert digest(folder/binary['path'])==binary['sha256']
                        output=destination/f'{side}-{pair}-{family}-{index}.jsonl'
                        log=destination/f'{side}-{pair}-{family}-{index}.log'
                        dll=folder/'bin'/point/family/'MethodCache.WarmupProbe.dll'
                        started=utc()
                        with log.open('x') as stream, output.open('x') as jsonstream:
                            child=subprocess.Popen(['dotnet',str(dll),case,str(inputs['warmups'])],env=env,stdout=jsonstream,stderr=stream,start_new_session=True)
                            while child.poll() is None:
                                check_lock();sample=snapshot(monitor);sample['phase']='measurement';observations.append(sample)
                                assert not sample['external'],sample
                                time.sleep(.5)
                            assert child.returncode==0
                            child=None
                        record=json.loads(output.read_text())
                        assert record['case']==case and record['actualIterations']==12 and len(record['actualMeasurements'])==12
                        assert record['warmupCount']==len(record['warmupMeasurements'])==inputs['warmups']
                        assert isinstance(record['ns'],(float,int)) and math.isfinite(record['ns']) and record['ns']>0
                        assert not record['serverGC'] and record['gcLatencyMode']=='Interactive' and record['runtime']=='.NET 10.0.3'
                        assert all(m['Operations']>0 and m['Nanoseconds']>0 for m in record['actualMeasurements'])
                        with (destination/'launches.jsonl').open('a') as receipt:
                            receipt.write(json.dumps({'side':side,'pair':pair,'family':family,'case':case,'point':point,'started_utc':started,'finished_utc':utc(),'output':output.name,'sha256':digest(output)})+'\n')
            print('Complete fixed pair',number,pair,utc(),flush=True)
        (destination/'exit').write_text('0\n')
        print('Complete independent comparison',number,utc(),flush=True)
    finally:
        (destination/'observations.json').write_text(json.dumps(observations)+'\n')

def costs():
    verify_inputs()
    prepared=json.loads((folder/'prepared.json').read_text())
    assert prepared['inputs_sha256']==digest(folder/'inputs.json')
    destination=folder/'cost-results';destination.mkdir()
    for side,point in [('base','baseline'),('head','final')]:
        for row in prepared['binaries'][point+'/int']:assert digest(folder/row['path'])==row['sha256']
        with (destination/(side+'.log')).open('x') as log:
            run(['dotnet',folder/'bin'/point/'int/MethodCache.IntProfile.dll','--out',destination/(side+'.jsonl')],stdout=log,stderr=subprocess.STDOUT,env=dict(os.environ,DOTNET_gcServer='0',DOTNET_gcConcurrent='1'))
        rows=[json.loads(line) for line in (destination/(side+'.jsonl')).read_text().splitlines()]
        assert len(rows)==48 and len({r['case'] for r in rows})==48
        assert all('error' not in r for r in rows)
    with (destination/'review.log').open('x') as log:
        result=subprocess.run(['python3',str(folder/'compare.py'),str(destination/'base.jsonl'),str(destination/'head.jsonl'),'--accepted',str(folder/'accepted.json'),'--markdown',str(destination/'report.md')],text=True,stdout=log,stderr=subprocess.STDOUT,timeout=60)
    (destination/'review.exit').write_text(str(result.returncode)+'\n')
    (destination/'exit').write_text('0\n')
    print('All48prefixcostcasesbothsidescomplete;reviewexit',result.returncode,utc(),flush=True)

def main():
    global deadline,monitor_module,child
    mode=sys.argv[1];verify_inputs()
    try:
        lock.mkdir()
    except FileExistsError:
        print('Existing coordinator reservation; nothing started',utc(),flush=True);raise SystemExit(75)
    (lock/'pid').write_text(str(os.getpid())+'\n')
    deadline=time.monotonic()+1470
    started=utc()
    print('Acquired bounded existing coordinator reservation',mode,os.getpid(),started,flush=True)
    try:
        spec=importlib.util.spec_from_file_location('activity',folder/'process_activity.py')
        monitor_module=importlib.util.module_from_spec(spec);spec.loader.exec_module(monitor_module)
        if mode=='prepare':prepare()
        elif mode=='measure':measure(sys.argv[2])
        elif mode=='cost':costs()
        else:raise ValueError(mode)
    finally:
        if child is not None and child.poll() is None:
            os.killpg(child.pid,signal.SIGTERM)
            try:child.wait(timeout=10)
            except subprocess.TimeoutExpired:os.killpg(child.pid,signal.SIGKILL);child.wait()
        if lock.exists() and (lock/'pid').read_text().strip()==str(os.getpid()):
            (lock/'pid').unlink();lock.rmdir()
        print('Released own coordinator reservation',mode,utc(),flush=True)

def terminated(signum, frame):
    raise SystemExit(128+signum)

signal.signal(signal.SIGTERM,terminated)
if __name__=='__main__':main()
