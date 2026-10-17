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
 label=owner+'-http-304-confirmation-'+name
 print('Waiting for exclusive capacity:',name,utc(),flush=True)
 # flock exits 75 on contention; timeout includes Docker, preparation, settling and measurement.
 # Cleanup runs inside the lease-owning child, before it releases the descriptor.
 cmd=['flock','--exclusive','--nonblock','--conflict-exit-code','75',lock,
      'python3',str(root/'leased-batch.py'),label,*container(label,command)]
 while True:
  with (folder/'logs'/(name+'.log')).open('a') as log:
   result=subprocess.run(cmd,stdout=log,stderr=subprocess.STDOUT)
  if result.returncode!=75:break
  time.sleep(30)
 if result.returncode:raise SystemExit('Batch failed '+name+' exit='+str(result.returncode))
 marker.write_text(utc());print('Finished',name,utc(),flush=True)
 # Give queued CI a chance to acquire the now-free shared/exclusive lease.
 time.sleep(10)
# Fresh fixed three-pair protocol. Never pool the preceding one-pair diagnostic.
(folder/'harness').mkdir(exist_ok=True);(folder/'results').mkdir(exist_ok=True)
for name in ['prefix','candidate']:
 link=folder/'harness'/name
 if not link.exists():link.symlink_to(root/'http-304-candidate/harness'/name,target_is_directory=True)
sources={n:json.loads((folder/'harness'/n/'source-manifest.json').read_text()) for n in ['prefix','candidate']}
assert sources['prefix']['source_sha256']==sources['candidate']['source_sha256']
assert sources['prefix']['runtime_sha']=='850cef8f3bc709eaf464b3c25e5ec898217fcc73'
assert sources['candidate']['runtime_sha']=='12bf99d877c33925fe0d0037f45e652d0260d8a4'
binaries={n:hashlib.sha256((folder/'harness'/n/'bin/Release/net10.0/DesignValidation.dll').read_bytes()).hexdigest() for n in sources}
metadata={'purpose':'Fixed three independent pairs, alternating prefix/candidate order, current HTTP 304 response-reuse candidate. No directional-pair samples pooled. Functional/native checks and whole-design acceptance remain separate.',
 'owner':owner,'image':image,'sources':sources,'binaries_sha256':binaries,
 'sdk':(root/'http-304-candidate/sdk-info.txt').read_text(),
 'cpu':subprocess.check_output(['lscpu'],text=True),'gc':'concurrent workstation','affinity':'unrestricted, identical for both revisions',
 'lease':lock,'batch_max_seconds':1500,'helpers_sha256':{str(p.relative_to(root)):hashlib.sha256(p.read_bytes()).hexdigest() for p in [folder/'run.py',folder/'worker.py',root/'leased-batch.py',root/'process_activity.py']},'runs':[]}
mp=folder/'results/metadata.json'
assert not mp.exists(), 'Fresh fixed protocol must not resume or mix a previous attempt'
mp.write_text(json.dumps(metadata,indent=2))
for round_number in range(1,4):
 order=['prefix','candidate'] if round_number%2 else ['candidate','prefix']
 for name in order:
  assert hashlib.sha256((folder/'harness'/name/'bin/Release/net10.0/DesignValidation.dll').read_bytes()).hexdigest()==binaries[name]
  start=utc();batch('round-'+str(round_number)+'-'+name,['python3',folder/'worker.py','worker',str(round_number),name,'revalidated'])
  metadata['runs'].append({'round':round_number,'revision':name,'group':'revalidated','started':start,'finished':utc(),'exit':0})
  mp.write_text(json.dumps(metadata,indent=2))
assert len(metadata['runs'])==6
print('Fixed HTTP 304 confirmation complete; review independent-run intervals and allocations, never infer acceptance from exit alone.',utc(),flush=True)
