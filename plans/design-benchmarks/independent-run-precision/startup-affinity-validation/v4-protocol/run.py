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
 label=owner+'-affinity-startup-v4-'+name
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
batch('verify-startup-affinity',['python3',folder/'prepare.py'])
(folder/'harness').mkdir(exist_ok=True);(folder/'results').mkdir(exist_ok=True)
link=folder/'harness/prefix'
if not link.exists():link.symlink_to(root/'net10-followup/harness/prefix',target_is_directory=True)
source=json.loads((link/'source-manifest.json').read_text());assert source['runtime_sha']=='850cef8f3bc709eaf464b3c25e5ec898217fcc73'
metadata={'purpose':'Fresh fixed nine-launch baseline precision study of all four hits. Parent starts on CPU2 before CLR; children inherit it. No BDN affinity characteristic is set. Actual OS CPU masks are sampled for all four benchmark child PIDs. Not acceptance.', 'fixed_launches':9, 'startup_probe':(folder/'startup-probe.txt').read_text(),
 'supersedes':'V3 stopped with validation exit1: short binary display100 was misparsed, and BDN attempted an invalid zero-mask affinity after seeing ProcessorCount1. Original V3 logs/data stay separate, not pooled. V4 uses startup taskset only and verifies actual OS masks; fresh fixed nine launches.','owner':owner,'image':image,'sources':{'prefix':source},'sdk':(root/'net10-followup/sdk-info.txt').read_text(),
 'cpu':subprocess.check_output(['lscpu'],text=True),'affinity_mask':4,'logical_cpu':2,'physical_core':2,
 'topology':'Observed 2026-10-15: CPU2 core2 sibling CPU10, max5200MHz; no host governor/boost/service changes.',
 'gc':'concurrent workstation','lease':lock,'batch_max_seconds':1500,
 'helpers_sha256':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in folder.glob('*.py')},'runs':[]}
mp=folder/'results/metadata.json'
if mp.exists():
 old=json.loads(mp.read_text());assert old['sources']==metadata['sources'] and old['helpers_sha256']==metadata['helpers_sha256'];metadata=old
else:mp.write_text(json.dumps(metadata,indent=2))
for rnd in range(1,10):
 start=utc();batch(f'round-{rnd}-prefix',['python3',folder/'worker.py','worker',str(rnd),'prefix','precision'])
 if not any(r['round']==rnd for r in metadata['runs']):
  metadata['runs'].append({'round':rnd,'revision':'prefix','group':'precision','started':start,'finished':utc(),'exit':0});mp.write_text(json.dumps(metadata,indent=2))
print('Nine-launch startup-affinity pilot complete. Review all means/intervals; no automatic acceptance.',utc(),flush=True)
