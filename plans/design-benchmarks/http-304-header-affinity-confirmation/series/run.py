"""Approved one-off Nimbus series. Exclusive nonblocking lease, <=25 min per batch.
No existing jobs are stopped. Only containers with this task's exact batch label are removed.
"""
from pathlib import Path
import datetime,hashlib,json,os,subprocess,time
folder=Path(__file__).resolve().parent
root=folder.parent
owner='methodcache-design-20261015-01a13a93'
image='sha256:28177d4fc98bf285dd0a62da2d22d2ef5f9e8d6adbc2500c8e49046b4ca4cdbf'
lock='/srv/skribera-ci/ci-build-capacity.lock'
assert (root/'OWNER').read_text().strip()==owner
revs={'http':'8ffa85cf85febd810753b450de47d7e7434b06f0','net10':'28ab9b22e02b0050a449c2bb1cfd977ec187ae25'}
(folder/'logs').mkdir(exist_ok=True);(root/'nuget').mkdir(exist_ok=True)
(root/'home').mkdir(exist_ok=True);(folder/'completed').mkdir(exist_ok=True)
uid=str(os.getuid());gid=str(os.getgid());docker_gid=str(os.stat('/var/run/docker.sock').st_gid)
def utc():return datetime.datetime.now(datetime.timezone.utc).isoformat()
def container(label,command):
 return ['docker','run','--rm','--init','--network','host','--pid','host','--user',uid+':'+gid,'--group-add',docker_gid,
 '--label','methodcache-design-bench='+label,'--volume',str(root)+':'+str(root),'--volume',str(root/'nuget')+':/nuget',
 '--volume',str(root/'home')+':/tmp/home','--volume','/var/run/docker.sock:/var/run/docker.sock',
 '--workdir',str(root),'--env','TESTCONTAINERS_HOST_OVERRIDE=localhost','--env','TESTCONTAINERS_RYUK_DISABLED=true',
 '--env','METHODCACHE_BENCH_LABEL='+label,image,*map(str,command)]
def batch(name,command):
 marker=folder/'completed'/name
 if marker.exists():return
 label=owner+'-http-304-header-affinity-v1-'+name
 print('Waiting for exclusive capacity:',name,utc(),flush=True)
 # flock exits 75 on contention; timeout includes Docker, preparation, settling and measurement.
 # Cleanup runs inside the lease-owning child, before it releases the descriptor.
 cmd=['flock','--exclusive','--nonblock','--conflict-exit-code','75',lock,
      'python3',str(root/'leased-batch.py'),label,*container(label,command)]
 while True:
  with (folder/'logs'/(name+'.log')).open('a') as log:
   result=subprocess.run(cmd,stdout=log,stderr=subprocess.STDOUT)
  if result.returncode!=75:break
  time.sleep(1)
 if result.returncode:raise SystemExit('Batch failed '+name+' exit='+str(result.returncode))
 marker.write_text(utc());print('Finished',name,utc(),flush=True)
 # Give queued CI a chance to acquire the now-free shared/exclusive lease.
 time.sleep(10)
# Every build and measurement is bounded by its own exclusive lease.
inputs=json.loads((folder/'frozen-inputs.json').read_text())
assert inputs['image']==image and inputs['rounds']==9 and inputs['launches']==18 and inputs['cases_per_launch']==4 and inputs['cpu_list']==[2]
for name,expected in inputs['files_sha256'].items():
 assert hashlib.sha256((folder/name).read_bytes()).hexdigest()==expected
assert hashlib.sha256((root/'leased-batch.py').read_bytes()).hexdigest()==inputs['leased_batch_sha256']
assert hashlib.sha256((root/'process_activity.py').read_bytes()).hexdigest()==inputs['process_activity_sha256']
parent=root/'http-304-header-v1'
prior_bytes=(parent/'results/metadata.json').read_bytes()
assert hashlib.sha256(prior_bytes).hexdigest()==inputs['prior_results_metadata_sha256']
prior=json.loads(prior_bytes)
assert len(prior['runs'])==6 and all(r['exit']==0 for r in prior['runs'])
(folder/'harness').mkdir(exist_ok=False)
for name in ['prefix','candidate']:
    target=parent/'harness'/name
    assert target.resolve().is_relative_to(root)
    assert hashlib.sha256((target/'source-manifest.json').read_bytes()).hexdigest()==inputs['source_manifest_sha256'][name]
    (folder/'harness'/name).symlink_to(target,target_is_directory=True)
(folder/'results').mkdir(exist_ok=True)
sources={n:json.loads((folder/'harness'/n/'source-manifest.json').read_text()) for n in ['prefix','candidate']}
assert sources['prefix']['source_sha256']==sources['candidate']['source_sha256']
assert sources['prefix']['runtime_sha']=='850cef8f3bc709eaf464b3c25e5ec898217fcc73'
assert sources['candidate']['runtime_sha']=='f0dd6c204cd3eefb8fb08ef0509340caf242da92'
assert sources['prefix']['harness_program_sha256']==sources['candidate']['harness_program_sha256']
binaries={n:{str(p.relative_to(folder/'harness'/n)):hashlib.sha256(p.read_bytes()).hexdigest()
    for p in sorted((folder/'harness'/n/'bin/Release/net10.0').rglob('*.dll'))} for n in sources}
assert binaries==inputs['binaries_sha256']
metadata={'purpose':'Fixed nine independent pairs with startup CPU2 affinity. Earlier completed three-pair header comparison has insufficient precision in all four cases and is retained separately; no samples pooled. Fixed count chosen before this cohort starts; no early stopping or selective samples. Full GUID timing and functional acceptance are separate.',
 'owner':owner,'image':image,'sources':sources,'binaries_sha256':binaries,'source_manifest_sha256':inputs['source_manifest_sha256'],
 'sdk':(folder/'sdk-info.txt').read_text(),
 'cpu':subprocess.check_output(['lscpu'],text=True),'gc':'concurrent workstation','affinity':'CPU2 via taskset before CLR startup; host and all four actual benchmark process CPU sets must be sampled and match [2]' ,
 'lease':lock,'batch_max_seconds':1500,'helpers_sha256':{str(p.relative_to(root)):hashlib.sha256(p.read_bytes()).hexdigest() for p in [folder/'run.py',folder/'worker.py',folder/'bdn_affinity.py',root/'leased-batch.py',root/'process_activity.py']},'runs':[]}
mp=folder/'results/metadata.json'
assert not mp.exists(), 'Fresh fixed protocol must not resume or mix a previous attempt'
mp.write_text(json.dumps(metadata,indent=2))
for round_number in range(1,10):
 order=['prefix','candidate'] if round_number%2 else ['candidate','prefix']
 for name in order:
  for relative,expected in binaries[name].items():
   assert hashlib.sha256((folder/'harness'/name/relative).read_bytes()).hexdigest()==expected
  for relative,expected in inputs['files_sha256'].items():
   assert hashlib.sha256((folder/relative).read_bytes()).hexdigest()==expected
  start=utc();batch('round-'+str(round_number)+'-'+name,['python3',folder/'worker.py','worker',str(round_number),name,'revalidated'])
  metadata['runs'].append({'round':round_number,'revision':name,'group':'revalidated','started':start,'finished':utc(),'exit':0})
  mp.write_text(json.dumps(metadata,indent=2))
assert len(metadata['runs'])==18
print('Fixed nine-pair CPU2 HTTP 304 confirmation complete; review independent-run intervals and allocations, never infer acceptance from exit alone.',utc(),flush=True)
