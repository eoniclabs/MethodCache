"""Current section-10 cost-only checks at actual PR edges; every operation inside a bounded lease."""
from pathlib import Path
import datetime,hashlib,json,os,secrets,subprocess,sys,time
folder=Path(__file__).resolve().parent
root=folder.parent
owner='methodcache-design-20261015-01a13a93'
assert (root/'OWNER').read_text().strip()==owner
inputs=json.loads((folder/'inputs.json').read_text())
image='sha256:800f1bd715743c32891eb73ba59d22a028d9d6a221234be54b440391a6c793e3'
redis_image='redis@sha256:858f009f9709ce576febc734aa78b8f6d624b82571f9ddb6bda4377c833b3499'
sql_image='mcr.microsoft.com/mssql/server@sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
utc=lambda:datetime.datetime.now(datetime.timezone.utc).isoformat()
def call(args,**kw):return subprocess.run(list(map(str,args)),check=True,text=True,**kw)
def capture(args,**kw):return subprocess.check_output(list(map(str,args)),text=True,**kw).strip()
def verify():
 assert sha(root/'leased-batch.py')=='4a83e0b505eaef6d91599479c8fa6c78ddc2958e698882740cca7ecad57a6830'
 for name,expected in inputs['frozen_sources'].items():assert sha(root/name)==expected,name
 assert sha(Path(__file__))==inputs['controller_sha256']
 if (folder/'started.json').exists():assert sha(folder/'inputs.json')==json.loads((folder/'started.json').read_text())['inputs_sha256']

def worker(number):
 verify()
 row=next(r for r in inputs['comparisons'] if r['pr']==number)
 destination=folder/f'pr-{number}';destination.mkdir()
 tree=destination/'tree'
 bundle=root/'final-paired-cost-v2/source.bundle'
 call(['git','clone','--no-checkout','--config','gc.auto=0','--config','maintenance.auto=false',bundle,tree],stdout=subprocess.DEVNULL)
 call(['git','-C',tree,'checkout','--detach',row['head']],stdout=subprocess.DEVNULL)
 assert capture(['git','-C',tree,'merge-base',row['base'],row['head']])==row['base']
 donor=root/'final-paired-cost-v2/tree'
 for relative in inputs['source_paths']:
  target=tree/relative;target.parent.mkdir(parents=True,exist_ok=True)
  target.write_bytes((donor/relative).read_bytes())
  target.chmod((donor/relative).stat().st_mode & 0o777)
 password='CostEdge_'+secrets.token_hex(16)+'!'
 private=dict(os.environ,MSSQL_SA_PASSWORD=password,SQLCMDPASSWORD=password)
 label=owner+f'-per-change-cost-pr-{number}'
 redis_name,sql_name=label+'-redis',label+'-sql'
 call(['docker','run','--detach','--label','methodcache-design-bench='+label,'--name',redis_name,'--publish','127.0.0.1::6379',redis_image],stdout=subprocess.DEVNULL)
 call(['docker','run','--detach','--label','methodcache-design-bench='+label,'--name',sql_name,'--publish','127.0.0.1::1433','--env','ACCEPT_EULA=Y','--env','MSSQL_SA_PASSWORD',sql_image],env=private,stdout=subprocess.DEVNULL)
 for _ in range(120):
  ready=subprocess.run(['docker','exec','--env','SQLCMDPASSWORD',sql_name,'/opt/mssql-tools18/bin/sqlcmd','-S','localhost','-U','sa','-C','-b','-Q','SELECT 1'],env=private,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
  if ready.returncode==0:break
  time.sleep(1)
 else:raise RuntimeError('Owned SQL backend not ready')
 call(['docker','exec',redis_name,'redis-cli','ping'],stdout=subprocess.DEVNULL)
 rp=capture(['docker','port',redis_name,'6379/tcp']).rsplit(':',1)[1]
 sp=capture(['docker','port',sql_name,'1433/tcp']).rsplit(':',1)[1]
 results=destination/'results';results.mkdir()
 env=dict(os.environ,METHODCACHE_REDIS_URL='localhost:'+rp,METHODCACHE_SQLSERVER_CONNECTION=f'Server=localhost,{sp};User Id=sa;Password={password};TrustServerCertificate=True;Encrypt=False',COST_PROFILE_OUT=str(results),COST_EDGE_BASE=row['base'])
 receipt={**row,'started_utc':utc(),'image':image,'redis_image':redis_image,'sql_image':sql_image,'controller_sha256':sha(Path(__file__)),'inputs_sha256':sha(folder/'inputs.json'),'source_bundle_sha256':sha(bundle),'source_hashes':{p:sha(tree/p) for p in inputs['source_paths']},'cpu':capture(['lscpu']),'cpus':list(range(16)),'criteria':'Unchanged current accepted.json; retain any original errors/failures; no timing runs or interpretation in this phase'}
 (destination/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n')
 args=['docker','run','--rm','--init','--network','host','--cpuset-cpus','0-15','--user',f'{os.getuid()}:{os.getgid()}','--label','methodcache-design-bench='+label,'--volume',str(root)+':'+str(root),'--volume',str(root/'nuget')+':/nuget','--volume',str(root/'home')+':/tmp/home','--workdir',str(tree)]
 for key in ['METHODCACHE_REDIS_URL','METHODCACHE_SQLSERVER_CONNECTION','COST_PROFILE_OUT','COST_EDGE_BASE']:args+=['--env',key]
 args +=[image,'bash','-lc','dotnet --info > "$COST_PROFILE_OUT/sdk-info.txt" && benchmarks/MethodCache.CostProfile/cost-profile.sh "$COST_EDGE_BASE"']
 code=subprocess.run(args,env=env).returncode
 receipt.update(finished_utc=utc(),profile_exit=code)
 (destination/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n')
 # A gate failure is retained and does not erase other independent PR evidence.
 (destination/'profile-exit').write_text(str(code)+'\n')


def main():
 if sys.argv[1]=='worker':worker(int(sys.argv[2]));return
 verify()
 assert not (folder/'started.json').exists()
 (folder/'started.json').write_text(json.dumps({'utc':utc(),'inputs_sha256':sha(folder/'inputs.json'),'controller_sha256':sha(Path(__file__))},indent=2)+'\n')
 for row in inputs['comparisons']:
  number=row['pr'];label=owner+f'-per-change-cost-pr-{number}'
  leased=['flock','--exclusive','--nonblock','--conflict-exit-code','75','/srv/skribera-ci/ci-build-capacity.lock','python3',str(root/'leased-batch.py'),label,'taskset','-c','0-15','python3','-B','-u',str(Path(__file__)),'worker',str(number)]
  print('Waiting for exclusive capacity',number,utc(),flush=True)
  while True:
   with (folder/f'pr-{number}.log').open('a') as log:code=subprocess.run(leased,stdout=log,stderr=subprocess.STDOUT).returncode
   if code!=75:break
   time.sleep(1)
  (folder/f'pr-{number}-worker-exit').write_text(str(code)+'\n')
  print('Finished cost worker',number,code,utc(),flush=True)
  time.sleep(10)
 (folder/'exit').write_text('0\n')
 print('All per-change cost workers terminal; inspect profile exits separately',utc(),flush=True)
if __name__=='__main__':main()
