from pathlib import Path
import datetime,hashlib,json,os,subprocess,sys,time
folder=Path(__file__).resolve().parent;root=folder.parent;sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest();utc=lambda:datetime.datetime.now(datetime.timezone.utc).isoformat();inputs=json.loads((folder/'inputs.json').read_text())
assert (root/'OWNER').read_text().strip()=='methodcache-design-20261015-01a13a93'
assert sha(root/'leased-batch.py')=='4a83e0b505eaef6d91599479c8fa6c78ddc2958e698882740cca7ecad57a6830'
for n,h in inputs['files_sha256'].items():assert sha(folder/n)==h,n

def worker():
 original=root/'final-paired-cost-v2';candidate=root/'eviction-hash-experiment-v1';prepared={s:json.loads((d/'prepared.json').read_text()) for s,d in [('base',original),('head',candidate)]};shipping={'base':original/'bin/base/main','head':candidate/'bin/head/main'};receipts={};binaries={};results=[]
 for side in ['base','head']:
  d=original if side=='base' else candidate;key='base/main' if side=='base' else 'head/main'
  for n,h in prepared[side]['binaries'][key].items():assert sha(d/n)==h,n
  receipts[side]={p.name:sha(p) for p in shipping[side].iterdir() if p.is_file()}
 label=inputs['label'];command=['docker','run','--rm','--init','--cpuset-cpus','0-15','--user',f'{os.getuid()}:{os.getgid()}','--label','methodcache-design-bench='+label,'--volume',str(root)+':'+str(root)+':ro','--volume',str(folder)+':'+str(folder),'--workdir',str(folder/'project'),'--env','DOTNET_CLI_HOME='+str(folder/'sdk-home'),'--env','DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1','--env','DOTNET_CLI_TELEMETRY_OPTOUT=1','--env','DOTNET_gcServer=0','--env','DOTNET_gcConcurrent=1',inputs['image']]
 logs=folder/'results';logs.mkdir()
 for side in ['base','head']:
  args=['dotnet','build','Profile.csproj','--no-incremental','-c','Release','-o',str(folder/'bin'/side),'-p:ShippingDir='+str(shipping[side]),'-p:Side='+side,'-nologo','-v:q']
  with (logs/(side+'-build.log')).open('x') as stream:code=subprocess.run(command+args,stdout=stream,stderr=subprocess.STDOUT).returncode
  (logs/(side+'-build.exit')).write_text(str(code)+'\n');assert code==0,(side,code)
  binaries[side]={str(p.relative_to(folder)):sha(p) for p in sorted((folder/'bin'/side).iterdir()) if p.is_file()}
  for name,h in receipts[side].items():
   target=folder/'bin'/side/name
   if name.startswith('MethodCache.') and name.endswith('.dll'):assert target.is_file() and sha(target)==h,(side,name)
 (folder/'prepared.json').write_text(json.dumps({'scope':inputs['purpose'],'inputs_sha256':sha(folder/'inputs.json'),'utc':utc(),'shipping_binaries':receipts,'diagnostic_builds':binaries,'base':prepared['base']['revisions']['base'],'head':prepared['head']['revisions']['head'],'head_patch_sha256':prepared['head']['head_patch_sha256']},indent=2)+'\n')
 index=0
 for listening in ['False','True']:
  for invalidation in ['False','True']:
   for side in (['base','head'] if index%2==0 else ['head','base']):
    output=logs/f'{side}-{listening}-{invalidation}.json';started=utc()
    with output.open('x') as stream:code=subprocess.run(command+['dotnet',str(folder/'bin'/side/'MethodCache.HttpCaching.Tests.dll'),listening,invalidation],stdout=stream,stderr=subprocess.STDOUT).returncode
    assert code==0,(side,listening,invalidation,code);data=json.loads(output.read_text());assert len(data['samples'])==10 and data['runtime']=='.NET 10.0.12' and not data['serverGC'] and data['gcLatencyMode']=='Interactive'
    for sample in data['samples']:assert sample['operations']==sample['originLoads']==8192
    for assembly in data['loadedLibraries']:
     if assembly['name']!='MethodCache.HttpCaching.Tests':assert receipts[side][Path(assembly['location']).name].upper()==assembly['sha256']
    results.append({'side':side,'listening':listening,'afterInvalidation':invalidation,'started_utc':started,'finished_utc':utc(),'path':str(output.relative_to(folder)),'sha256':sha(output)})
   index+=1
 for side in ['base','head']:
  for name,h in receipts[side].items():assert sha(shipping[side]/name)==h
 (folder/'validation.json').write_text(json.dumps({'utc':utc(),'scope':inputs['purpose'],'inputs_sha256':sha(folder/'inputs.json'),'prepared_sha256':sha(folder/'prepared.json'),'results':results,'all_shipping_files_unchanged':True},indent=2)+'\n');(folder/'worker.exit').write_text('0\n');print('All8diagnosticprocesses complete;NOTtimingacceptance',utc(),flush=True)

if sys.argv[1]=='worker':worker()
else:
 assert not (folder/'started.json').exists();(folder/'started.json').write_text(json.dumps({'utc':utc(),'inputs_sha256':sha(folder/'inputs.json')})+'\n')
 while True:
  with (folder/'worker.log').open('a') as stream:code=subprocess.run(['flock','--exclusive','--nonblock','--conflict-exit-code','75','/srv/skribera-ci/ci-build-capacity.lock','python3',str(root/'leased-batch.py'),inputs['label'],'python3','-B','-u',str(Path(__file__)),'worker'],stdout=stream,stderr=subprocess.STDOUT).returncode
  if code!=75:break
  time.sleep(5)
 (folder/'exit').write_text(str(code)+'\n');print('Bounded diagnostic terminal',code,utc(),flush=True)
