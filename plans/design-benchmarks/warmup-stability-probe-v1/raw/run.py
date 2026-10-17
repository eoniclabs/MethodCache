from pathlib import Path
import datetime,hashlib,importlib.util,json,os,subprocess,sys,time
folder=Path(__file__).resolve().parent;root=folder.parent;sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest();utc=lambda:datetime.datetime.now(datetime.timezone.utc).isoformat();inputs=json.loads((folder/'inputs.json').read_text());assert (root/'OWNER').read_text().strip()=='methodcache-design-20261015-01a13a93';assert sha(root/'leased-batch.py')=='4a83e0b505eaef6d91599479c8fa6c78ddc2958e698882740cca7ecad57a6830'
for n,h in inputs['files_sha256'].items():assert sha(folder/n)==h,n

def worker():
 originals={'base':root/'final-paired-cost-v2','head':root/'eviction-hash-experiment-v1'};shipping={side:p/('bin/base/main' if side=='base' else 'bin/head/main') for side,p in originals.items()};receipts={};binaries={};logs=folder/'results';logs.mkdir()
 for side,p in originals.items():
  d=json.loads((p/'prepared.json').read_text())
  for n,h in d['binaries'][side+'/main'].items():assert sha(p/n)==h
  receipts[side]={f.name:sha(f) for f in shipping[side].iterdir() if f.is_file()}
 command=['docker','run','--rm','--init','--cpuset-cpus','0-15','--user',f'{os.getuid()}:{os.getgid()}','--label','methodcache-design-bench='+inputs['label'],'--volume',str(root)+':'+str(root)+':ro','--volume',str(folder)+':'+str(folder),'--workdir',str(folder/'project'),'--env','DOTNET_CLI_HOME='+str(folder/'sdk-home'),'--env','DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1','--env','DOTNET_CLI_TELEMETRY_OPTOUT=1','--env','DOTNET_gcServer=0','--env','DOTNET_gcConcurrent=1',inputs['image']]
 for side in ['base','head']:
  with (logs/(side+'-build.log')).open('x') as log:code=subprocess.run(command+['dotnet','build','Profile.csproj','--no-incremental','-c','Release','-o',str(folder/'bin'/side),'-p:ShippingDir='+str(shipping[side]),'-p:Side='+side,'-nologo','-v:q'],stdout=log,stderr=subprocess.STDOUT).returncode
  (logs/(side+'-build.exit')).write_text(str(code)+'\n');assert code==0,(side,code)
  binaries[side]={str(p.relative_to(folder)):sha(p) for p in sorted((folder/'bin'/side).iterdir()) if p.is_file()}
  for n,h in receipts[side].items():
   if n.startswith('MethodCache.') and n.endswith('.dll'):assert sha(folder/'bin'/side/n)==h
 (folder/'prepared.json').write_text(json.dumps({'inputs_sha256':sha(folder/'inputs.json'),'utc':utc(),'shipping_binaries':receipts,'diagnostic_builds':binaries,'scope':inputs['purpose']},indent=2)+'\n')
 spec=importlib.util.spec_from_file_location('activity',folder/'process_activity.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);monitor=module.ActivityMonitor();observations=[]
 def observe(phase):
  external=monitor.observe(subprocess.check_output(['ps','-eo','pid=,ppid=,stat=,pcpu=,time=,etime=,args='],text=True),owned_pid=os.getpid());observations.append({'utc':utc(),'phase':phase,'external':external});assert not external,'Competing build/test work; preserve diagnostic attempt'
 for _ in range(60):observe('preflight');time.sleep(1)
 results=[]
 try:
  for index,item in enumerate(inputs['launches']):
   side=item['side'];output=logs/f'{index:02d}-{side}-warm{item["warmups"]}.json';started=utc()
   with output.open('x') as log:
    child=subprocess.Popen(command+['dotnet',str(folder/'bin'/side/'MethodCache.WarmupProbe.dll'),item['case'],str(item['warmups'])],stdout=log,stderr=subprocess.STDOUT)
    while child.poll() is None:observe('measurement');time.sleep(.5)
    assert child.returncode==0,(index,child.returncode)
   x=json.loads(output.read_text());assert x['case']==item['case'] and x['warmupCount']==len(x['warmupMeasurements'])==item['warmups'] and x['actualIterations']==len(x['actualMeasurements'])==12;assert x['runtime']=='.NET 10.0.12' and not x['serverGC'] and x['gcLatencyMode']=='Interactive';assert x['affinityMask']>0 and not x['affinityMask'] & ~65535
   for a in x['libraries']:
    if a['name']!='MethodCache.WarmupProbe':assert receipts[side][Path(a['location']).name].upper()==a['sha256']
   results.append({**item,'path':str(output.relative_to(folder)),'sha256':sha(output),'started_utc':started,'finished_utc':utc()})
 finally:(folder/'observations.json').write_text(json.dumps(observations)+'\n')
 for side in ['base','head']:
  for n,h in receipts[side].items():assert sha(shipping[side]/n)==h
 (folder/'validation.json').write_text(json.dumps({'utc':utc(),'inputs_sha256':sha(folder/'inputs.json'),'prepared_sha256':sha(folder/'prepared.json'),'results':results,'scope':inputs['purpose'],'all_shipping_files_unchanged':True},indent=2)+'\n');(folder/'worker.exit').write_text('0\n');print('All8warmupdiagnosticprocessescomplete;NOTpairedacceptance',utc(),flush=True)

if sys.argv[1]=='worker':worker()
else:
 assert not (folder/'started.json').exists();(folder/'started.json').write_text(json.dumps({'utc':utc(),'inputs_sha256':sha(folder/'inputs.json')})+'\n')
 while True:
  with (folder/'worker.log').open('a') as log:code=subprocess.run(['flock','--exclusive','--nonblock','--conflict-exit-code','75','/srv/skribera-ci/ci-build-capacity.lock','python3',str(root/'leased-batch.py'),inputs['label'],'python3','-B','-u',str(Path(__file__)),'worker'],stdout=log,stderr=subprocess.STDOUT).returncode
  if code!=75:break
  time.sleep(5)
 (folder/'exit').write_text(str(code)+'\n');print('Warmupdiagnosticterminal',code,utc(),flush=True)
