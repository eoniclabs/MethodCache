from pathlib import Path
import datetime,hashlib,json,os,subprocess,sys,time
folder=Path(__file__).resolve().parent;root=folder.parent;sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest();utc=lambda:datetime.datetime.now(datetime.timezone.utc).isoformat();inputs=json.loads((folder/'inputs.json').read_text())
assert (root/'OWNER').read_text().strip()=='methodcache-design-20261015-01a13a93'
assert sha(root/'leased-batch.py')=='4a83e0b505eaef6d91599479c8fa6c78ddc2958e698882740cca7ecad57a6830'
for n,h in inputs['files_sha256'].items():assert sha(folder/n)==h,n

def worker():
 old=root/'final-paired-cost-v2';candidate=root/'http-deferred-id-experiment-v1'
 base=json.loads((old/'prepared.json').read_text());head=json.loads((candidate/'prepared.json').read_text())
 assert base['source_hashes']['base']==head['source_hashes']['head']
 assert head['baseline_patch_sha256']==inputs['candidate_patches'][0] and head['head_patch_sha256']==inputs['candidate_patches'][1]
 assert (candidate/'prepare.exit').read_text().strip()=='0'
 assert (root/'http-deferred-id-time32-v1/results/group-00/exit').read_text().strip()=='0' and (root/'http-deferred-id-time32-v1/results/group-01/exit').read_text().strip()=='0'
 sources={'base':old/'base','head':candidate/'tree'}
 assert subprocess.check_output(['git','-C',str(sources['base']),'rev-parse','HEAD'],text=True).strip()==inputs['base']
 (folder/'tree').symlink_to(candidate/'tree',target_is_directory=True)
 production={};bins={};shipping_receipts={};logs=folder/'preparation';logs.mkdir()
 command=['docker','run','--rm','--init','--cpuset-cpus','0-15','--user',f'{os.getuid()}:{os.getgid()}','--label','methodcache-design-bench='+inputs['prepare_label'],'--volume',str(root)+':'+str(root)+':ro','--volume',str(folder)+':'+str(folder),'--workdir',str(folder/'project'),'--env','DOTNET_CLI_HOME='+str(folder/'sdk-home'),'--env','DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1','--env','DOTNET_CLI_TELEMETRY_OPTOUT=1',inputs['image']]
 for side in ['base','head']:
  prior=base if side=='base' else head;origin=old if side=='base' else candidate
  for n,h in prior['source_hashes'][side].items():assert sha(sources[side]/n)==h,n
  for n,h in prior['binaries'][side+'/main'].items():assert sha(origin/n)==h,n
  shipping=origin/'bin'/side/'main';shipping_receipts[side]={p.name:sha(p) for p in shipping.iterdir() if p.is_file()}
  names=subprocess.check_output(['git','-C',str(sources[side]),'ls-files','src/'],text=True).splitlines();production[side]=[{'path':n,'sha256':sha(sources[side]/n)} for n in names if (sources[side]/n).is_file()]
  with (logs/(side+'-build.log')).open('x') as log:code=subprocess.run(command+['dotnet','build','Profile.csproj','--no-incremental','-c','Release','-o',str(folder/'bin'/side/'main'),'-p:ShippingDir='+str(shipping),'-p:Side='+side,'-nologo','-v:q'],stdout=log,stderr=subprocess.STDOUT).returncode
  (logs/(side+'-build.exit')).write_text(str(code)+'\n');assert code==0,(side,code)
  for name,h in shipping_receipts[side].items():
   if name.endswith('.dll'):assert sha(folder/'bin'/side/'main'/name)==h,name
  bins[side+'/main']={str(p.relative_to(folder)):sha(p) for p in sorted((folder/'bin'/side/'main').iterdir()) if p.is_file()}
 groups=[[{'family':'main','case':c} for c in inputs['cases'][i:i+2]] for i in range(0,len(inputs['cases']),2)]
 (folder/'prepared.json').write_text(json.dumps({'inputs_sha256':sha(folder/'inputs.json'),'prepared_utc':utc(),'revisions':{'base':inputs['base'],'head':inputs['head']},'head_patches_sha256':inputs['candidate_patches'],'source_hashes':{'base':base['source_hashes']['base'],'head':head['source_hashes']['head']},'production_sources':production,'shipping_binaries':shipping_receipts,'binaries':bins,'groups':groups,'sdk':base['sdk'],'cpu':base['cpu'],'protocol':inputs['purpose']},indent=2)+'\n')
 (folder/'worker-prepare.exit').write_text('0\n');print('Cumulative14casehelpersprepared;verifyallinputsindependentlybeforemeasurement',utc(),flush=True)

if sys.argv[1]=='worker':worker()
else:
 assert not (folder/'prepare-started.json').exists();(folder/'prepare-started.json').write_text(json.dumps({'utc':utc(),'inputs_sha256':sha(folder/'inputs.json')})+'\n')
 while True:
  with (folder/'prepare-worker.log').open('a') as log:code=subprocess.run(['flock','--exclusive','--nonblock','--conflict-exit-code','75','/srv/skribera-ci/ci-build-capacity.lock','python3',str(root/'leased-batch.py'),inputs['prepare_label'],'python3','-B','-u',str(Path(__file__)),'worker'],stdout=log,stderr=subprocess.STDOUT).returncode
  if code!=75:break
  time.sleep(5)
 (folder/'prepare.exit').write_text(str(code)+'\n');print('Cumulative14preparationterminal',code,utc(),flush=True)
