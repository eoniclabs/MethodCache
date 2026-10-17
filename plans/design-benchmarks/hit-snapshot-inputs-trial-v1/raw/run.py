"""One final paired cohort, split into bounded capacity leases without discarding launches."""
from pathlib import Path
import datetime, hashlib, importlib.util, json, math, os, secrets, subprocess, sys, time

folder = Path(__file__).resolve().parent
root = folder.parent
inputs = json.loads((folder/'inputs.json').read_text())
owner = 'methodcache-design-20261015-01a13a93'
assert (root/'OWNER').read_text().strip() == owner
digest = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
utc = lambda: datetime.datetime.now(datetime.timezone.utc).isoformat()
inputs_hash = digest(folder/'inputs.json')

def run(args, **kw):
    return subprocess.run(list(map(str,args)),check=True,text=True,**kw)

def capture(args, **kw):
    return subprocess.check_output(list(map(str,args)),text=True,**kw).strip()

def verify_inputs():
    assert digest(folder/'inputs.json') == inputs_hash
    assert digest(root/'leased-batch.py') == '4a83e0b505eaef6d91599479c8fa6c78ddc2958e698882740cca7ecad57a6830'
    for relative, expected in inputs['files_sha256'].items():
        assert digest(folder/relative) == expected,relative

def adapter(tree):
    source=tree/'benchmarks/MethodCache.CostProfile'
    target=tree/'benchmarks/MethodCache.IntProfile';target.mkdir()
    for p in source.glob('*.cs'):
        (target/p.name).write_bytes(p.read_bytes())
    project=(source/'MethodCache.CostProfile.csproj').read_text()
    project=project.replace('<ItemGroup>\n    <Compile Include=', '<ItemGroup>\n    <Compile Include="../MethodCache.Benchmarks/Microbenchmarks/GeneratedPrefixHitPathBenchmarks.cs" Link="Harness/GeneratedPrefixHitPathBenchmarks.cs" />\n    <Compile Include=',1)
    (target/'MethodCache.IntProfile.csproj').write_text(project)
    program=(target/'Program.cs').read_text()
    start=program.index('    private static readonly Type[] Types =')
    end=program.index('    public static async Task<int> Main',start)
    program=program[:start]+'    private static readonly Type[] Types = [typeof(MethodCache.Benchmarks.Microbenchmarks.GeneratedPrefixHitPathBenchmarks)];\n\n'+program[end:]
    start=program.index('        if (args.Contains("--list"))')
    end=program.index('        using var redis =',start)
    program=program[:start]+'''        if (args.Contains("--list"))
        {
            foreach (var c in BenchmarkDotNet.Running.BenchmarkConverter.TypeToBenchmarks(Types[0]).BenchmarksCases)
            {
                var name = $"{c.Descriptor.Type.Name}.{c.Descriptor.WorkloadMethod.Name}({string.Join(",", c.Parameters.Items.Select(p => $"{p.Name}={p.Value}"))})";
                if (Matches(name)) Console.WriteLine(name);
            }
            return 0;
        }
'''+program[end:]
    (target/'Program.cs').write_text(program)
    (target/'PrefixSetup.cs').write_text('namespace MethodCache.Benchmarks.Microbenchmarks;\ninternal static class PrefixBenchmarkSetup { internal static void PreferPerformanceCores() => DesignWorkloadSetup.PreferPerformanceCores(); }\n')

def prepare():
    verify_inputs()
    head=folder/'tree';base=folder/'base'
    run(['git','-C',head,'worktree','add','--detach',base,inputs['base']])
    workloads=['Comparison/GeneratedContractBenchmarks.cs','Microbenchmarks/DesignWorkloadBenchmarks.cs',
        'Microbenchmarks/HttpResponsePathBenchmarks.cs','Microbenchmarks/GeneratedPrefixHitPathBenchmarks.cs']
    target=base/'benchmarks/MethodCache.CostProfile';target.mkdir(parents=True,exist_ok=True)
    for p in (head/'benchmarks/MethodCache.CostProfile').iterdir():
        if p.suffix in ('.cs','.csproj'): (target/p.name).write_bytes(p.read_bytes())
    for relative in workloads:
        p=base/'benchmarks/MethodCache.Benchmarks'/relative;p.parent.mkdir(parents=True,exist_ok=True)
        p.write_bytes((head/'benchmarks/MethodCache.Benchmarks'/relative).read_bytes())
    inventories={};source_hashes={};binaries={}
    env=dict(os.environ,METHODCACHE_REDIS_URL='localhost:1',METHODCACHE_SQLSERVER_CONNECTION='inventory-only')
    for side,tree in [('base',base),('head',head)]:
        assert capture(['git','-C',tree,'rev-parse','HEAD']) == inputs[side]
        adapter(tree)
        source_hashes[side]={str(p.relative_to(tree)):digest(p) for p in sorted((tree/'benchmarks/MethodCache.IntProfile').glob('*')) if p.is_file()}
        source_hashes[side].update({str(p.relative_to(tree)):digest(p) for p in [tree/'benchmarks/MethodCache.Benchmarks'/w for w in workloads]})
        inventories[side]={}
        for family,project in [('main','MethodCache.CostProfile'),('int','MethodCache.IntProfile')]:
            output=folder/'bin'/side/family
            run(['dotnet','build',tree/'benchmarks'/project/(project+'.csproj'),'-c','Release','-o',output,
                '-p:NBGV_GitEngine=Disabled','-nologo','-v:q','-clp:ErrorsOnly'])
            dll=output/(project+'.dll')
            names=capture(['dotnet',dll,'--list'],env=env,cwd=tree).splitlines()
            if family=='int': names=[n for n in names if n.startswith('GeneratedPrefixHitPathBenchmarks.SyncHit(') and 'ArgumentKind=Int' in n]
            assert len(names)==len(set(names))==({'main':67,'int':4}[family]),(side,family,names)
            inventories[side][family]=names
            binaries[side+'/'+family]={str(p.relative_to(folder)):digest(p) for p in sorted(output.glob('*')) if p.is_file()}
    assert inventories['base']==inventories['head']
    assert source_hashes['base']==source_hashes['head']
    selected=[{'family':family,'case':name} for family in ('main','int') for name in inventories['head'][family]]
    groups=[]
    for family in ('main','int'):
        family_cases=[item for item in selected if item['family']==family]
        groups.extend(family_cases[i:i+4] for i in range(0,len(family_cases),4))
    prepared={'inputs_sha256':digest(folder/'inputs.json'),'prepared_utc':utc(),'revisions':{s:inputs[s] for s in ('base','head')},
        'source_hashes':source_hashes,'binaries':binaries,'inventories':inventories,'groups':groups,
        'sdk':capture(['dotnet','--info']),'cpu':capture(['lscpu']),
        'protocol':'10 independent launches per side and case; each case in a new process; base first odd pairs, head first even; fixed 4 warmups, 12 actual 150ms iterations; no partial mean review; same pinned backend images per native group; 60s quiet before timing; <=1470s lease per group; fixed 0-15 cpuset; workstation concurrent GC'}
    assert not (folder/'prepared.json').exists()
    (folder/'prepared.json').write_text(json.dumps(prepared,indent=2)+'\n')
    print('Prepared and frozen',len(selected),'cases in',len(groups),'bounded groups',utc(),flush=True)

def snapshot(monitor):
    output=capture(['ps','-eo','pid=,ppid=,stat=,pcpu=,time=,etime=,args='])
    return monitor.observe(output,owned_pid=os.getpid())

def measure(group_index,label):
    verify_inputs()
    prepared=json.loads((folder/'prepared.json').read_text())
    assert prepared['inputs_sha256']==digest(folder/'inputs.json')
    group=prepared['groups'][group_index]
    destination=folder/'results'/f'group-{group_index:02d}';destination.mkdir(parents=True)
    spec=importlib.util.spec_from_file_location('activity',folder/'process_activity.py')
    activity=importlib.util.module_from_spec(spec);spec.loader.exec_module(activity)
    monitor=activity.ActivityMonitor()
    for _ in range(60):
        assert not snapshot(monitor),'External work appeared before timing; preserve attempt'
        time.sleep(1)
    env=dict(os.environ,DOTNET_gcServer='0',DOTNET_gcConcurrent='1')
    native=any('Backend=Redis' in item['case'] or 'Backend=SqlServer' in item['case'] for item in group)
    if native:
        password='Paired_'+secrets.token_hex(16)+'!'
        private=dict(os.environ,MSSQL_SA_PASSWORD=password,SQLCMDPASSWORD=password)
        redis_name,sql_name=label+'-redis',label+'-sql'
        for name,image,port,extra in [(redis_name,inputs['redis_image'],'6379',[]),(sql_name,inputs['sql_image'],'1433',['--env','ACCEPT_EULA=Y','--env','MSSQL_SA_PASSWORD'])]:
            run(['docker','run','--detach','--name',name,'--label','methodcache-design-bench='+label,'--publish','127.0.0.1::'+port,*extra,image],env=private,stdout=subprocess.DEVNULL)
        for _ in range(120):
            p=subprocess.run(['docker','exec','--env','SQLCMDPASSWORD',sql_name,'/opt/mssql-tools18/bin/sqlcmd','-S','localhost','-U','sa','-C','-b','-Q','SELECT 1'],env=private,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
            if p.returncode==0:break
            time.sleep(1)
        else:raise RuntimeError('Owned SQL backend readiness failed')
        rp=capture(['docker','port',redis_name,'6379/tcp']).rsplit(':',1)[1]
        sp=capture(['docker','port',sql_name,'1433/tcp']).rsplit(':',1)[1]
        env.update(METHODCACHE_REDIS_URL='localhost:'+rp,METHODCACHE_SQLSERVER_CONNECTION=f'Server=localhost,{sp};User Id=sa;Password={password};TrustServerCertificate=True;Encrypt=False')
    observations=[]
    try:
        for pair in range(1,inputs['pairs']+1):
            order=['base','head'] if pair%2 else ['head','base']
            for case_index,item in enumerate(group):
                for side in order:
                    family=item['family'];output=destination/f'{side}-{pair}-{case_index}.jsonl'
                    dll=folder/'bin'/side/family/('MethodCache.CostProfile.dll' if family=='main' else 'MethodCache.IntProfile.dll')
                    for relative,expected in prepared['binaries'][side+'/'+family].items():assert digest(folder/relative)==expected
                    assert not output.exists()
                    started=utc()
                    with (destination/f'{side}-{pair}-{case_index}.log').open('x') as log:
                        child=subprocess.Popen(['dotnet',str(dll),item['case'],'--exact','--time','--out',str(output)],cwd=folder/'tree',env=env,stdout=log,stderr=subprocess.STDOUT)
                        while child.poll() is None:
                            external=snapshot(monitor)
                            observed={}
                            try:
                                for task in Path(f'/proc/{child.pid}/task').iterdir():
                                    try:affinity=sorted(os.sched_getaffinity(int(task.name)))
                                    except ProcessLookupError:continue
                                    assert set(affinity).issubset(set(range(16)))
                                    observed[task.name]=affinity
                            except FileNotFoundError:pass
                            observations.append({'utc':utc(),'child':child.pid,'external':external,'thread_affinities':observed})
                            assert not external,'External build/test work overlapped; preserve attempt'
                            time.sleep(.5)
                        assert child.returncode==0,(side,pair,item['case'],child.returncode)
                    records=[json.loads(line) for line in output.read_text().splitlines()]
                    assert len(records)==1 and records[0]['case']==item['case'],(item,records)
                    record=records[0]
                    assert isinstance(record['ns'],(float,int)) and math.isfinite(record['ns']) and record['ns']>0,record
                    assert record['actualIterations']==len(record['actualMeasurements'])==12
                    assert not record['serverGC']
                    assert all(m['Operations']>0 and m['Nanoseconds']>0 for m in record['actualMeasurements'])
                    with (destination/'launches.jsonl').open('a') as file:file.write(json.dumps({'side':side,'pair':pair,'case':item['case'],'family':family,'started_utc':started,'finished_utc':utc(),'output':output.name,'sha256':digest(output),'runtime':record['runtime'],'serverGC':record['serverGC'],'gcLatencyMode':record['gcLatencyMode']})+'\n')
            print('Completed group',group_index,'pair',pair,utc(),flush=True)
        (destination/'exit').write_text('0\n')
    finally:
        (destination/'observations.json').write_text(json.dumps(observations)+'\n')

def leased(mode,index=None):
    verify_inputs()
    suffix='prepare' if index is None else f'group-{index:02d}'
    label=owner+'-'+folder.name+'-'+suffix
    uid,gid,docker_gid=map(str,[os.getuid(),os.getgid(),os.stat('/var/run/docker.sock').st_gid])
    args=['docker','run','--rm','--init','--network','host','--pid','host','--cpuset-cpus','0-15',
        '--user',uid+':'+gid,'--group-add',docker_gid,'--label','methodcache-design-bench='+label,
        '--volume',str(root)+':'+str(root),'--volume',str(root/'nuget')+':/nuget','--volume',str(root/'home')+':/tmp/home',
        '--volume','/var/run/docker.sock:/var/run/docker.sock','--workdir',str(folder),'--env','TESTCONTAINERS_HOST_OVERRIDE=localhost',
        inputs['image'],'python3','-B','-u',str(folder/'run.py'),mode]
    if index is not None:args.extend([str(index),label])
    cmd=['flock','--exclusive','--nonblock','--conflict-exit-code','75','/srv/skribera-ci/ci-build-capacity.lock',
        'python3',str(root/'leased-batch.py'),label,*args]
    print('Waiting for capacity',suffix,utc(),flush=True)
    while True:
        verify_inputs()
        with (folder/(suffix+'.log')).open('a') as log:code=subprocess.run(cmd,stdout=log,stderr=subprocess.STDOUT).returncode
        if code!=75:break
        time.sleep(2)
    print('Finished',suffix,'exit',code,utc(),flush=True)
    assert code==0,'Preserve this failed attempt; do not restart or overwrite its outputs'

def main():
    mode=sys.argv[1]
    if mode=='worker-prepare':prepare();return
    if mode=='worker-measure':measure(int(sys.argv[2]),sys.argv[3]);return
    verify_inputs()
    if mode=='prepare':
        assert not (folder/'tree').exists()
        run(['git','clone','--no-checkout',folder/'source.bundle',folder/'tree'])
        run(['git','-C',folder/'tree','config','gc.auto','0'])
        run(['git','-C',folder/'tree','checkout','--detach',inputs['head']])
        leased('worker-prepare')
    elif mode=='run':
        assert not (folder/'results').exists()
        prepared=json.loads((folder/'prepared.json').read_text())
        (folder/'timing-start.json').write_text(json.dumps({'utc':utc(),'prepared_sha256':digest(folder/'prepared.json'),'inputs_sha256':digest(folder/'inputs.json')},indent=2)+'\n')
        for index in inputs['remaining_groups']:
            leased('worker-measure',index)
            time.sleep(10)
        (folder/'exit').write_text('0\n')
        print('All fixed paired timing groups completed; analysis remains required',utc(),flush=True)
    else:raise ValueError(mode)

if __name__=='__main__':main()
