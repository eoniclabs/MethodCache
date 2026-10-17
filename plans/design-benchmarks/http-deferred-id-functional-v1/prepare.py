from pathlib import Path
import datetime,hashlib,json,os,subprocess,sys,time
folder=Path(__file__).resolve().parent;root=folder.parent;inputs=json.loads((folder/'inputs.json').read_text());sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest();utc=lambda:datetime.datetime.now(datetime.timezone.utc).isoformat()
assert (root/'OWNER').read_text().strip()=='methodcache-design-20261015-01a13a93'
assert sha(root/'leased-batch.py')=='4a83e0b505eaef6d91599479c8fa6c78ddc2958e698882740cca7ecad57a6830'
for name,value in inputs['files_sha256'].items():assert sha(folder/name)==value

def worker():
 parent=root/'eviction-hash-experiment-v1';prior=json.loads((parent/'prepared.json').read_text());tree=folder/'tree';assert prior['revisions']['head']==inputs['head'];assert sha(parent/'runtime.patch')==inputs['baseline_patch_sha256'];subprocess.run(['git','-c','gc.auto=0','-c','maintenance.auto=false','-C',str(parent/'tree'),'worktree','add','--detach',str(tree),inputs['head']],check=True);subprocess.run(['git','-C',str(tree),'apply',str(parent/'runtime.patch')],check=True)
 for n,h in prior['source_hashes']['head'].items():
  p=parent/'tree'/n;assert sha(p)==h;q=tree/n;q.parent.mkdir(parents=True,exist_ok=True);q.write_bytes(p.read_bytes())
 for n,h in json.loads((folder/'source-intent.json').read_text())['original_sources'].items():assert sha(tree/n)==h
 test=tree/'tests/MethodCache.HttpCaching.Tests/Storage/HttpDeferredIdentityTests.cs';test.write_bytes((folder/'HttpDeferredIdentityTests.cs').read_bytes())
 results=folder/'preparation';results.mkdir();command=['docker','run','--rm','--init','--cpuset-cpus','0-15','--user',f'{os.getuid()}:{os.getgid()}','--label','methodcache-design-bench='+inputs['prepare_label'],'--volume',str(root)+':'+str(root),'--volume',str(root/'nuget')+':/nuget','--volume',str(root/'home')+':/tmp/home','--workdir',str(tree),inputs['image']]
 def phase(name,args):
  with (results/(name+'.log')).open('x') as log:code=subprocess.run(command+args,stdout=log,stderr=subprocess.STDOUT).returncode
  (results/(name+'.exit')).write_text(str(code)+'\n');assert code==0,(name,code)
 baseline_sources={n:sha(tree/n) for n in subprocess.check_output(['git','-C',str(tree),'ls-files','src/'],text=True).splitlines() if (tree/n).is_file()}
 common=['-c','Release','-f','net10.0','-p:NBGV_GitEngine=Disabled','--logger','trx']
 phase('baseline-admin-semantics',['dotnet','test','tests/MethodCache.HttpCaching.Tests/MethodCache.HttpCaching.Tests.csproj',*common,'--filter','FullyQualifiedName~HttpDeferredIdentityTests','--results-directory',str(results/'baseline-admin')])
 phase('baseline-build',['dotnet','build','benchmarks/MethodCache.CostProfile/MethodCache.CostProfile.csproj','-c','Release','-o',str(folder/'bin/base/main'),'-p:NBGV_GitEngine=Disabled','-nologo','-v:q'])
 subprocess.run(['git','-C',str(tree),'apply',str(folder/'deferred-id.patch')],check=True)
 expected=json.loads((folder/'source-intent.json').read_text())['candidate_sources']
 for n,h in expected.items():assert sha(tree/n)==h
 head_sources={n:sha(tree/n) for n in baseline_sources};assert {n for n in baseline_sources if baseline_sources[n]!=head_sources[n]}==set(expected)
 phase('candidate-admin-semantics',['dotnet','test','tests/MethodCache.HttpCaching.Tests/MethodCache.HttpCaching.Tests.csproj',*common,'--filter','FullyQualifiedName~HttpDeferredIdentityTests','--results-directory',str(results/'candidate-admin')])
 phase('candidate-http-all',['dotnet','test','tests/MethodCache.HttpCaching.Tests/MethodCache.HttpCaching.Tests.csproj',*common,'--no-build','--results-directory',str(results/'candidate-http')])
 phase('candidate-core-all',['dotnet','test','tests/MethodCache.Core.Tests/MethodCache.Core.Tests.csproj',*common,'--results-directory',str(results/'candidate-core')])
 phase('candidate-build',['dotnet','build','benchmarks/MethodCache.CostProfile/MethodCache.CostProfile.csproj','-c','Release','-o',str(folder/'bin/head/main'),'-p:NBGV_GitEngine=Disabled','-nologo','-v:q'])
 binaries={side+'/main':{str(p.relative_to(folder)):sha(p) for p in sorted((folder/'bin'/side/'main').iterdir()) if p.is_file()} for side in ['base','head']};groups=[[{'family':'main','case':c} for c in inputs['cases'][i:i+4]] for i in range(0,len(inputs['cases']),4)];assert len(groups)==2
 for side in ['base','head']:
  dll=folder/'bin'/side/'main/MethodCache.CostProfile.dll';available=subprocess.check_output(command+['dotnet',str(dll),'--list'],text=True).splitlines();assert set(inputs['cases'])<=set(available)
 prepared={'inputs_sha256':sha(folder/'inputs.json'),'prepared_utc':utc(),'revisions':{'base':inputs['base'],'head':inputs['head']},'baseline_patch_sha256':inputs['baseline_patch_sha256'],'head_patch_sha256':inputs['candidate_patch_sha256'],'source_hashes':{s:prior['source_hashes']['head'] for s in ['base','head']},'production_sources':{side:[{'path':n,'sha256':h} for n,h in rows.items()] for side,rows in [('base',baseline_sources),('head',head_sources)]},'binaries':binaries,'groups':groups,'sdk':prior['sdk'],'cpu':prior['cpu'],'protocol':'Isolated candidate comparison,all8predeclaredColdMiss/Revalidatedsettings,10alternatingpairspercase,freshprocess4warmups12actual150ms,same pinnedSDK/runtime/GC/affinity/quietguard. No nodehash/security/function removals; extra admin tests pass both sides.'};(folder/'prepared.json').write_text(json.dumps(prepared,indent=2)+'\n');(folder/'worker-prepare.exit').write_text('0\n');print('Candidate semantics andfullHTTP/Core tests pass; independentlyverifybeforetiming',utc(),flush=True)

if sys.argv[1]=='worker':worker()
else:
 assert not (folder/'prepare-started.json').exists();(folder/'prepare-started.json').write_text(json.dumps({'utc':utc(),'inputs_sha256':sha(folder/'inputs.json')})+'\n')
 while True:
  with (folder/'prepare-worker.log').open('a') as log:code=subprocess.run(['flock','--exclusive','--nonblock','--conflict-exit-code','75','/srv/skribera-ci/ci-build-capacity.lock','python3',str(root/'leased-batch.py'),inputs['prepare_label'],'python3','-B','-u',str(Path(__file__)),'worker'],stdout=log,stderr=subprocess.STDOUT).returncode
  if code!=75:break
  time.sleep(5)
 (folder/'prepare.exit').write_text(str(code)+'\n');print('Candidate preparation terminal',code,utc(),flush=True)
