from pathlib import Path
import datetime,hashlib,json,os,secrets,subprocess,sys,time
folder=Path(__file__).resolve().parent;root=folder.parent;owner='methodcache-design-20261015-01a13a93';label=owner+'-eviction-cost-v1'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
utc=lambda:datetime.datetime.now(datetime.timezone.utc).isoformat()
assert (root/'OWNER').read_text().strip()==owner
inputs=json.loads((folder/'inputs.json').read_text())
def call(args,**kw):return subprocess.run(list(map(str,args)),check=True,text=True,**kw)
def capture(args,**kw):return subprocess.check_output(list(map(str,args)),text=True,**kw).strip()
def verify():
 assert sha(folder/'run.py')==inputs['controller_sha256']
 for side,data in inputs['sources'].items():
  assert sha(root/data['prepared'])==data['prepared_sha256']
  for relative,expected in data['binaries'].items():assert sha(root/relative)==expected,relative
 assert sha(folder/'accepted.json')==inputs['accepted_sha256']
 assert sha(folder/'compare.py')==inputs['compare_sha256']
def worker():
 verify();results=folder/'results';results.mkdir()
 password='Profile_'+secrets.token_hex(16)+'!';private=dict(os.environ,MSSQL_SA_PASSWORD=password,SQLCMDPASSWORD=password)
 rn,sn=label+'-redis',label+'-sql'
 for name,image,port,extra in [(rn,inputs['redis_image'],'6379',[]),(sn,inputs['sql_image'],'1433',['--env','ACCEPT_EULA=Y','--env','MSSQL_SA_PASSWORD'])]:
  call(['docker','run','--detach','--label','methodcache-design-bench='+label,'--name',name,'--publish','127.0.0.1::'+port,*extra,image],env=private,stdout=subprocess.DEVNULL)
 for _ in range(120):
  p=subprocess.run(['docker','exec','--env','SQLCMDPASSWORD',sn,'/opt/mssql-tools18/bin/sqlcmd','-S','localhost','-U','sa','-C','-b','-Q','SELECT 1'],env=private,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
  if p.returncode==0:break
  time.sleep(1)
 else:raise RuntimeError('Owned SQL backend readiness failed')
 call(['docker','exec',rn,'redis-cli','ping'],stdout=subprocess.DEVNULL)
 rp=capture(['docker','port',rn,'6379/tcp']).rsplit(':',1)[1];sp=capture(['docker','port',sn,'1433/tcp']).rsplit(':',1)[1]
 env=dict(os.environ,METHODCACHE_REDIS_URL='localhost:'+rp,METHODCACHE_SQLSERVER_CONNECTION=f'Server=localhost,{sp};User Id=sa;Password={password};TrustServerCertificate=True;Encrypt=False',DOTNET_gcServer='0',DOTNET_gcConcurrent='1')
 receipt={'started_utc':utc(),'inputs_sha256':sha(folder/'inputs.json'),'cpu':capture(['lscpu']),'backend_scope':'New exact-label containers only; no shared services','cost_rows_per_side':67}
 for side,data in inputs['sources'].items():
  verify()
  args=['docker','run','--rm','--init','--network','host','--cpuset-cpus','0-15','--user',f'{os.getuid()}:{os.getgid()}','--label','methodcache-design-bench='+label,'--volume',str(root)+':'+str(root),'--workdir',str(root/data['tree'])]
  for key in ['METHODCACHE_REDIS_URL','METHODCACHE_SQLSERVER_CONNECTION','DOTNET_gcServer','DOTNET_gcConcurrent']:args+=['--env',key]
  args +=[inputs['image'],'dotnet',str(root/data['dll']),'--out',str(results/(side+'.jsonl'))]
  with (results/(side+'.log')).open('x') as log:call(args,env=env,stdout=log,stderr=subprocess.STDOUT)
  rows=[json.loads(line) for line in (results/(side+'.jsonl')).read_text().splitlines()];assert len(rows)==67 and len({r['case'] for r in rows})==67 and all('error' not in r for r in rows)
 verify()
 review=subprocess.run(['python3',str(folder/'compare.py'),str(results/'base.jsonl'),str(results/'head.jsonl'),'--accepted',str(folder/'accepted.json'),'--markdown',str(results/'report.md')],text=True,capture_output=True)
 (results/'review.log').write_text(review.stdout+review.stderr);(results/'review.exit').write_text(str(review.returncode)+'\n')
 receipt.update(finished_utc=utc(),review_exit=review.returncode,binary_hashes_before_and_after='all222verified')
 (folder/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n');assert review.returncode==0
if sys.argv[1]=='worker':worker()
else:
 verify();assert not (folder/'started.json').exists();(folder/'started.json').write_text(json.dumps({'utc':utc(),'inputs_sha256':sha(folder/'inputs.json')})+'\n')
 while True:
  with (folder/'worker.log').open('a') as log:p=subprocess.run(['flock','--exclusive','--nonblock','--conflict-exit-code','75','/srv/skribera-ci/ci-build-capacity.lock','python3',str(root/'leased-batch.py'),label,'python3','-B','-u',str(folder/'run.py'),'worker'],stdout=log,stderr=subprocess.STDOUT)
  if p.returncode!=75:break
  time.sleep(2)
 (folder/'exit').write_text(str(p.returncode)+'\n');print('Candidate cumulative cost terminal',p.returncode,utc(),flush=True)
