from pathlib import Path
import datetime,hashlib,json,os,secrets,subprocess,sys,time
f=Path(__file__).resolve().parent;r=f.parent;owner='methodcache-design-20261015-01a13a93';label=owner+'-policy-deadline-v1';sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest();utc=lambda:datetime.datetime.now(datetime.timezone.utc).isoformat();i=json.loads((f/'inputs.json').read_text())
assert (r/'OWNER').read_text().strip()==owner
assert sha(f/'run.py')==i['controller_sha256'] and sha(f/'test.patch')==i['patch_sha256'] and sha(r/i['bundle'])==i['bundle_sha256']
def call(args,**kw):return subprocess.run(list(map(str,args)),check=True,**kw)
def capture(args,**kw):return subprocess.check_output(list(map(str,args)),text=True,**kw).strip()
def worker():
 tree=f/'tree';call(['git','clone','--no-checkout','--config','gc.auto=0','--config','maintenance.auto=false',r/i['bundle'],tree]);call(['git','-C',tree,'checkout','--detach',i['base']]);results=f/'results';results.mkdir()
 password='Deadline_'+secrets.token_hex(16)+'!';private=dict(os.environ,MSSQL_SA_PASSWORD=password,SQLCMDPASSWORD=password);rn,sn=label+'-redis',label+'-sql'
 for name,image,port,extra in [(rn,i['redis_image'],'6379',[]),(sn,i['sql_image'],'1433',['--env','ACCEPT_EULA=Y','--env','MSSQL_SA_PASSWORD'])]:call(['docker','run','--detach','--label','methodcache-design-bench='+label,'--name',name,'--publish','127.0.0.1::'+port,*extra,image],env=private,stdout=subprocess.DEVNULL)
 for _ in range(120):
  p=subprocess.run(['docker','exec','--env','SQLCMDPASSWORD',sn,'/opt/mssql-tools18/bin/sqlcmd','-S','localhost','-U','sa','-C','-b','-Q','SELECT 1'],env=private,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
  if p.returncode==0:break
  time.sleep(1)
 else:raise RuntimeError('Owned SQL readiness failed')
 rp=capture(['docker','port',rn,'6379/tcp']).rsplit(':',1)[1];sp=capture(['docker','port',sn,'1433/tcp']).rsplit(':',1)[1]
 env=dict(os.environ,METHODCACHE_REDIS_URL='localhost:'+rp,METHODCACHE_SQLSERVER_CONNECTION=f'Server=localhost,{sp};User Id=sa;Password={password};TrustServerCertificate=True;Encrypt=False',METHODCACHE_REQUIRE_INFRA_TESTS='true')
 container=['docker','run','--rm','--init','--network','host','--cpuset-cpus','0-15','--user',f'{os.getuid()}:{os.getgid()}','--label','methodcache-design-bench='+label,'--volume',str(r)+':'+str(r),'--volume',str(r/'nuget')+':/nuget','--volume',str(r/'home')+':/tmp/home','--workdir',str(tree)]
 for key in ['METHODCACHE_REDIS_URL','METHODCACHE_SQLSERVER_CONNECTION','METHODCACHE_REQUIRE_INFRA_TESTS']:container+=['--env',key]
 container+=[i['image']];runs=[];binaries={}
 for side in ['base','head']:
  if side=='head':call(['git','-C',tree,'apply',f/'test.patch'])
  families=['Redis','SqlServer'] if side=='head' else ['Redis']
  for family in families:
   project=f'tests/MethodCache.{family}.IntegrationTests/MethodCache.{family}.IntegrationTests.csproj'
   with (results/f'{side}-{family}-build.log').open('x') as log:call(container+['dotnet','build',project,'-c','Release','-f','net10.0','-p:NBGV_GitEngine=Disabled','-nologo','-v:q'],env=env,stdout=log,stderr=subprocess.STDOUT)
   binaries[side+family]={'core_sha256':sha(tree/f'tests/MethodCache.{family}.IntegrationTests/bin/Release/net10.0/MethodCache.Core.dll'),'tests_sha256':sha(tree/f'tests/MethodCache.{family}.IntegrationTests/bin/Release/net10.0/MethodCache.{family}.IntegrationTests.dll')}
   for run in range(1,11 if family=='Redis' else 4):
    name=f'{side}-{family}-{run:02d}';log=results/(name+'.log')
    with log.open('x') as stream:p=subprocess.run(container+['dotnet','test',project,'-c','Release','-f','net10.0','--no-build','--no-restore','--filter','FullyQualifiedName~PolicyTransitionsAndDeadlines_ConformToSharedContract','--collect:XPlat Code Coverage','--results-directory',str(results/name),'--logger','trx;LogFileName=result.trx','-p:NBGV_GitEngine=Disabled'],env=env,stdout=stream,stderr=subprocess.STDOUT)
    runs.append({'side':side,'backend':family,'run':run,'exit':p.returncode,'log':log.name,'log_sha256':sha(log)});print('Freshnativecontract',name,'exit',p.returncode,utc(),flush=True)
 assert binaries['baseRedis']['core_sha256']==binaries['headRedis']['core_sha256']==binaries['headSqlServer']['core_sha256']
 with (results/'core-tests.log').open('x') as log:p=subprocess.run(container+['dotnet','test','tests/MethodCache.Core.Tests/MethodCache.Core.Tests.csproj','-c','Release','-f','net10.0','-p:NBGV_GitEngine=Disabled'],env=env,stdout=log,stderr=subprocess.STDOUT)
 receipt={'utc':utc(),'inputs_sha256':sha(f/'inputs.json'),'runs':runs,'binaries':binaries,'core_tests_exit':p.returncode,'core_dll_identical':True,'native_scope':'OwnexactlabelnewpinnedRedis/SQLcontainers;sharedservicesuntouched','scope':'Nativepolicycontractwithcoveragecollector;all23freshprocessresultsretained. No clock assertion, expiry value, timeout or runtime code is weakened.'};(f/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n');assert p.returncode==0 and all(x['exit']==0 for x in runs if x['side']=='head')
if sys.argv[1]=='worker':worker()
else:
 assert not (f/'started.json').exists();(f/'started.json').write_text(json.dumps({'utc':utc(),'inputs_sha256':sha(f/'inputs.json')})+'\n')
 while True:
  with (f/'worker.log').open('a') as log:p=subprocess.run(['flock','--exclusive','--nonblock','--conflict-exit-code','75','/srv/skribera-ci/ci-build-capacity.lock','python3',str(r/'leased-batch.py'),label,'python3','-B','-u',str(f/'run.py'),'worker'],stdout=log,stderr=subprocess.STDOUT)
  if p.returncode!=75:break
  time.sleep(2)
 (f/'exit').write_text(str(p.returncode)+'\n');print('Policydeadlineexperimentterminal',p.returncode,utc(),flush=True)
