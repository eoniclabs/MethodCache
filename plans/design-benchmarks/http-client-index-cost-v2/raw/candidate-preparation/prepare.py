from pathlib import Path
import datetime,hashlib,json,os,subprocess,sys,time,shutil
folder=Path(__file__).resolve().parent;root=folder.parent;sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest();utc=lambda:datetime.datetime.now(datetime.timezone.utc).isoformat()
inputs=json.loads((folder/'inputs.json').read_text())
assert (root/'OWNER').read_text().strip()=='methodcache-design-20261015-01a13a93'
assert sha(root/'leased-batch.py')=='4a83e0b505eaef6d91599479c8fa6c78ddc2958e698882740cca7ecad57a6830'
for n,h in inputs['files_sha256'].items():assert sha(folder/n)==h,n

def worker():
 old=root/'http-final14-compact-v1';prior=json.loads((old/'prepared.json').read_text());tree=folder/'tree'
 intent=json.loads((folder/'source-intent.json').read_text());assert prior['production_sources']['head']==intent['parent_production']
 subprocess.run(['git','-c','gc.auto=0','-c','maintenance.auto=false','-C',str(old/'tree'),'worktree','add','--detach',str(tree),inputs['head']],check=True)
 for row in intent['parent_production']:
  source=old/'tree'/row['path'];assert sha(source)==row['sha256'];target=tree/row['path'];target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(source.read_bytes())
 assert sha(folder/'production.patch')==intent['production_patch_sha256']
 subprocess.run(['git','-C',str(tree),'apply',str(folder/'production.patch')],check=True)
 for n,h in intent['candidate_sources'].items():assert sha(tree/n)==h,n
 source_bodies=old/'frozen-harness-source'
 for p in source_bodies.rglob('*'):
  if p.is_file() and 'MethodCache.IntProfile' not in p.parts:
   rel=p.relative_to(source_bodies);target=tree/rel;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(p.read_bytes())
 names=subprocess.check_output(['git','-C',str(tree),'ls-files','--cached','--others','--exclude-standard','src/'],text=True).splitlines()
 production=[{'path':n,'sha256':sha(tree/n)} for n in sorted(set(names)) if (tree/n).is_file()]
 before={n['path']:n['sha256'] for n in prior['production_sources']['head']};after={n['path']:n['sha256'] for n in production}
 assert {n for n in set(before)|set(after) if before.get(n)!=after.get(n)}==set(json.loads((folder/'source-intent.json').read_text())['candidate_sources'])
 logs=folder/'preparation';logs.mkdir()
 command=['docker','run','--rm','--init','--cpuset-cpus','0-15','--user',f'{os.getuid()}:{os.getgid()}','--label','methodcache-design-bench='+inputs['prepare_label'],'--volume',str(root)+':'+str(root),'--volume',str(root/'nuget')+':/nuget','--volume',str(root/'home')+':/tmp/home','--workdir',str(tree),inputs['image']]
 with (logs/'head-build.log').open('x') as log:code=subprocess.run(command+['dotnet','build','benchmarks/MethodCache.CostProfile/MethodCache.CostProfile.csproj','-c','Release','-o',str(folder/'bin/head/main'),'-p:NBGV_GitEngine=Disabled','-nologo','-v:q'],stdout=log,stderr=subprocess.STDOUT).returncode
 (logs/'head-build.exit').write_text(str(code)+'\n');assert code==0
 for n,h in prior['binaries']['base/main'].items():assert sha(old/n)==h,n
 shutil.copytree(old/'bin/base/main',folder/'bin/base/main')
 # Run the exact previously compiled common-public-API helper on both sides.
 for suffix in ['dll','pdb','deps.json','runtimeconfig.json']:
  name='MethodCache.WarmupProbe.'+suffix;shutil.copyfile(old/'bin/base/main'/name,folder/'bin/head/main'/name)
 bins={s+'/main':{str(p.relative_to(folder)):sha(p) for p in sorted((folder/'bin'/s/'main').iterdir()) if p.is_file()} for s in ['base','head']}
 sources={str(p.relative_to(tree)):sha(p) for p in sorted((tree/'benchmarks/MethodCache.CostProfile').glob('*')) if p.suffix in ['.cs','.csproj']}
 for rel in ['Comparison/GeneratedContractBenchmarks.cs','Microbenchmarks/DesignWorkloadBenchmarks.cs','Microbenchmarks/HttpResponsePathBenchmarks.cs','Microbenchmarks/GeneratedPrefixHitPathBenchmarks.cs']:
  n='benchmarks/MethodCache.Benchmarks/'+rel;sources[n]=sha(tree/n)
 prepared={'inputs_sha256':sha(folder/'inputs.json'),'prepared_utc':utc(),'revisions':{'base':inputs['base'],'head':inputs['head']},'source_hashes':{'base':sources,'head':sources},'production_sources':{'base':prior['production_sources']['base'],'head':production},'binaries':bins,'groups':[[{'family':'main','case':c} for c in inputs['cases']]],'sdk':prior['sdk'],'cpu':prior['cpu'],'protocol':inputs['purpose'],'helper_original_sha256':sha(old/'bin/base/main/MethodCache.WarmupProbe.dll')}
 prepared['source_equivalent_parent']=intent['source_equivalent_parent'];prepared['client_index_patch_sha256']=intent['production_patch_sha256']
 (folder/'prepared.json').write_text(json.dumps(prepared,indent=2)+'\n');(folder/'worker-prepare.exit').write_text('0\n')
 print('Current source plus client-index trial prepared; no measurement started',utc(),flush=True)
if sys.argv[1]=='worker':worker()
else:
 assert not (folder/'prepare-started.json').exists();(folder/'prepare-started.json').write_text(json.dumps({'utc':utc(),'inputs_sha256':sha(folder/'inputs.json')})+'\n')
 while True:
  with (folder/'prepare-worker.log').open('a') as log:code=subprocess.run(['flock','--exclusive','--nonblock','--conflict-exit-code','75','/srv/skribera-ci/ci-build-capacity.lock','python3',str(root/'leased-batch.py'),inputs['prepare_label'],'python3','-B','-u',str(Path(__file__)),'worker'],stdout=log,stderr=subprocess.STDOUT).returncode
  if code!=75:break
  time.sleep(5)
 (folder/'prepare.exit').write_text(str(code)+'\n');print('Preparation terminal',code,utc(),flush=True)
