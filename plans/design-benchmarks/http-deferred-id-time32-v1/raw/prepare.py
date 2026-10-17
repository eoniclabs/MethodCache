from pathlib import Path
import datetime,hashlib,json,os,subprocess,sys,time
folder=Path(__file__).resolve().parent;root=folder.parent;inputs=json.loads((folder/'inputs.json').read_text());sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest();utc=lambda:datetime.datetime.now(datetime.timezone.utc).isoformat();assert (root/'OWNER').read_text().strip()=='methodcache-design-20261015-01a13a93';assert sha(root/'leased-batch.py')=='4a83e0b505eaef6d91599479c8fa6c78ddc2958e698882740cca7ecad57a6830'
for n,h in inputs['files_sha256'].items():assert sha(folder/n)==h,n

def worker():
 original=root/'http-deferred-id-experiment-v1';assert (original/'prepare.exit').read_text().strip()=='0';old=json.loads((original/'prepared.json').read_text());receipts={};bins={};logs=folder/'preparation';logs.mkdir();(folder/'tree').symlink_to(original/'tree',target_is_directory=True)
 command=['docker','run','--rm','--init','--cpuset-cpus','0-15','--user',f'{os.getuid()}:{os.getgid()}','--label','methodcache-design-bench='+inputs['prepare_label'],'--volume',str(root)+':'+str(root)+':ro','--volume',str(folder)+':'+str(folder),'--workdir',str(folder/'project'),'--env','DOTNET_CLI_HOME='+str(folder/'sdk-home'),'--env','DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1','--env','DOTNET_CLI_TELEMETRY_OPTOUT=1',inputs['image']]
 for side in ['base','head']:
  shipping=original/'bin'/side/'main'
  for n,h in old['binaries'][side+'/main'].items():assert sha(original/n)==h
  receipts[side]={p.name:sha(p) for p in shipping.iterdir() if p.is_file()}
  with (logs/(side+'-build.log')).open('x') as log:code=subprocess.run(command+['dotnet','build','Profile.csproj','--no-incremental','-c','Release','-o',str(folder/'bin'/side/'main'),'-p:ShippingDir='+str(shipping),'-p:Side='+side,'-nologo','-v:q'],stdout=log,stderr=subprocess.STDOUT).returncode
  (logs/(side+'-build.exit')).write_text(str(code)+'\n');assert code==0,(side,code)
  for name,h in receipts[side].items():
   if name.endswith('.dll'):assert sha(folder/'bin'/side/'main'/name)==h,(side,name)
  bins[side+'/main']={str(p.relative_to(folder)):sha(p) for p in sorted((folder/'bin'/side/'main').iterdir()) if p.is_file()}
 groups=[[{'family':'main','case':case} for case in inputs['cases'][i:i+4]] for i in range(0,len(inputs['cases']),4)]
 (folder/'prepared.json').write_text(json.dumps({'inputs_sha256':sha(folder/'inputs.json'),'prepared_utc':utc(),'revisions':old['revisions'],'baseline_patch_sha256':old['baseline_patch_sha256'],'head_patch_sha256':old['head_patch_sha256'],'source_hashes':old['source_hashes'],'production_sources':old['production_sources'],'shipping_binaries':receipts,'binaries':bins,'groups':groups,'sdk':old['sdk'],'cpu':old['cpu'],'protocol':'All8fixedaffectedColdMiss/Revalidatedcases,10alternatingpairs,freshprocesspercase/side/pair;32warmups/12actual150ms,originalcompiledbenchmarkandlibraryfilesunchangedwhenaddinghelper. Sameprocess/runtimeGC/actualthreadcpuset/interferenceguards,60quietseconds,1470secondleases. Originalshortwarmupreportsretained.'},indent=2)+'\n');(folder/'worker-prepare.exit').write_text('0\n');print('Same measured libraries and workloaddlls;32warmuphelperprepared,verifybeforetiming',utc(),flush=True)

if sys.argv[1]=='worker':worker()
else:
 assert not (folder/'prepare-started.json').exists();(folder/'prepare-started.json').write_text(json.dumps({'utc':utc(),'inputs_sha256':sha(folder/'inputs.json')})+'\n')
 while True:
  with (folder/'prepare-worker.log').open('a') as log:code=subprocess.run(['flock','--exclusive','--nonblock','--conflict-exit-code','75','/srv/skribera-ci/ci-build-capacity.lock','python3',str(root/'leased-batch.py'),inputs['prepare_label'],'python3','-B','-u',str(Path(__file__)),'worker'],stdout=log,stderr=subprocess.STDOUT).returncode
  if code!=75:break
  time.sleep(5)
 (folder/'prepare.exit').write_text(str(code)+'\n');print('Time-helperpreparationterminal',code,utc(),flush=True)
