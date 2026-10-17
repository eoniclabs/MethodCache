from pathlib import Path
import datetime,hashlib,json,os,subprocess,sys,time
f=Path(__file__).resolve().parent;r=f.parent;owner='methodcache-design-20261015-01a13a93';label=owner+'-docs-hit-probe-v1';sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest();utc=lambda:datetime.datetime.now(datetime.timezone.utc).isoformat();i=json.loads((f/'inputs.json').read_text())
assert (r/'OWNER').read_text().strip()==owner
assert sha(f/'run.py')==i['controller_sha256'] and sha(f/'test.patch')==i['patch_sha256'] and sha(f/'source.bundle')==i['bundle_sha256']
def worker():
 tree=f/'tree';subprocess.run(['git','clone','--no-checkout','--config','gc.auto=0','--config','maintenance.auto=false',str(f/'source.bundle'),str(tree)],check=True);subprocess.run(['git','-C',str(tree),'checkout','--detach',i['base']],check=True)
 container=['docker','run','--rm','--init','--cpuset-cpus','0-15','--user',f'{os.getuid()}:{os.getgid()}','--label','methodcache-design-bench='+label,'--volume',str(r)+':'+str(r),'--volume',str(r/'nuget')+':/nuget','--volume',str(r/'home')+':/tmp/home','--workdir',str(tree),i['image']]
 project='tests/MethodCache.Docs.Tests/MethodCache.Docs.Tests.csproj';results=f/'results';results.mkdir();runs=[];binaries={}
 for side in ['base','head']:
  if side=='head':subprocess.run(['git','-C',str(tree),'apply',str(f/'test.patch')],check=True)
  with (results/(side+'-build.log')).open('x') as log:subprocess.run(container+['dotnet','build',project,'-c','Release','-f','net10.0','-p:NBGV_GitEngine=Disabled','-nologo','-v:q'],check=True,stdout=log,stderr=subprocess.STDOUT)
  binaries[side]={'core_sha256':sha(tree/'tests/MethodCache.Docs.Tests/bin/Release/net10.0/MethodCache.Core.dll'),'docs_tests_sha256':sha(tree/'tests/MethodCache.Docs.Tests/bin/Release/net10.0/MethodCache.Docs.Tests.dll'),'test_source_sha256':sha(tree/'tests/MethodCache.Docs.Tests/TelemetryHitAllocationTests.cs')}
  if side=='head':assert binaries['head']['core_sha256']==binaries['base']['core_sha256']
  for run in range(1,11):
   name=f'{side}-{run:02d}';log=results/(name+'.log')
   with log.open('x') as stream:p=subprocess.run(container+['dotnet','test',project,'-c','Release','-f','net10.0','--no-build','--no-restore','--filter','FullyQualifiedName~TelemetryHitAllocationTests','--results-directory',str(results/name),'--logger','trx;LogFileName=result.trx','-p:NBGV_GitEngine=Disabled'],stdout=stream,stderr=subprocess.STDOUT)
   runs.append({'side':side,'run':run,'exit':p.returncode,'log':log.name,'log_sha256':sha(log)});print('Freshtestprocess',name,'exit',p.returncode,utc(),flush=True)
 with (results/'full-docs.log').open('x') as stream:p=subprocess.run(container+['dotnet','test',project,'-c','Release','-f','net10.0','--no-build','--no-restore','-p:NBGV_GitEngine=Disabled'],stdout=stream,stderr=subprocess.STDOUT)
 receipt={'utc':utc(),'inputs_sha256':sha(f/'inputs.json'),'runs':runs,'binaries':binaries,'full_docs_exit':p.returncode,'source_scope':'Onlythetestprobeharnesschanges;identicalCoreDLLhashbothsides','scope':'Fresh-processunitregressiondiagnostic;notalatencygate. OriginalCI4480bytefailureisretainedseparately.'};(f/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n');assert p.returncode==0 and all(run['exit']==0 for run in runs if run['side']=='head')
if sys.argv[1]=='worker':worker()
else:
 assert not (f/'started.json').exists();(f/'started.json').write_text(json.dumps({'utc':utc(),'inputs_sha256':sha(f/'inputs.json')})+'\n')
 while True:
  with (f/'worker.log').open('a') as log:p=subprocess.run(['flock','--exclusive','--nonblock','--conflict-exit-code','75','/srv/skribera-ci/ci-build-capacity.lock','python3',str(r/'leased-batch.py'),label,'python3','-B','-u',str(f/'run.py'),'worker'],stdout=log,stderr=subprocess.STDOUT)
  if p.returncode!=75:break
  time.sleep(2)
 (f/'exit').write_text(str(p.returncode)+'\n');print('Docshitprobeexperimentterminal',p.returncode,utc(),flush=True)
