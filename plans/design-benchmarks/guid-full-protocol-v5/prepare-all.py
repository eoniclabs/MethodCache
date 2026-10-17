"""Approved one-off, bounded exclusive preparation. No measurement dispatch."""
from pathlib import Path
import datetime
import hashlib
import json
import os
import subprocess
import time

folder = Path(__file__).resolve().parent
root = folder.parent
owner = 'methodcache-design-20261015-01a13a93'
image = 'sha256:28177d4fc98bf285dd0a62da2d22d2ef5f9e8d6adbc2500c8e49046b4ca4cdbf'
lock = '/srv/skribera-ci/ci-build-capacity.lock'
assert (root/'OWNER').read_text().strip() == owner
inputs = json.loads((folder/'frozen-inputs.json').read_text())
assert inputs['image'] == image
for name, digest in inputs['protocol_sha256'].items():
    assert hashlib.sha256((folder/name).read_bytes()).hexdigest() == digest
assert hashlib.sha256((root/'leased-batch.py').read_bytes()).hexdigest() == inputs['leased_batch_sha256']
for name in ('logs', 'completed'): (folder/name).mkdir(exist_ok=True)
uid = str(os.getuid()); gid = str(os.getgid()); docker_gid = str(os.stat('/var/run/docker.sock').st_gid)


def utc(): return datetime.datetime.now(datetime.timezone.utc).isoformat()


def batch(name, command):
    marker = folder/'completed'/name
    if marker.exists(): return
    label = owner+'-guid-final-v5-'+name
    container = ['docker','run','--rm','--init','--network','host','--pid','host','--user',uid+':'+gid,
        '--group-add',docker_gid,'--label','methodcache-design-bench='+label,
        '--volume',str(root)+':'+str(root),'--volume',str(root/'nuget')+':/nuget',
        '--volume',str(root/'home')+':/tmp/home','--volume','/var/run/docker.sock:/var/run/docker.sock',
        '--workdir',str(folder),'--env','TESTCONTAINERS_HOST_OVERRIDE=localhost',
        '--env','TESTCONTAINERS_RYUK_DISABLED=true','--env','METHODCACHE_BENCH_LABEL='+label,'--env','METHODCACHE_BENCH_BACKEND_RECEIPT='+str(folder/(name+'-backends.json')),image,*map(str,command)]
    leased = ['flock','--exclusive','--nonblock','--conflict-exit-code','75',lock,
              'python3',str(root/'leased-batch.py'),label,*container]
    print('Waiting for exclusive capacity:',name,utc(),flush=True)
    while True:
        with (folder/'logs'/(name+'.log')).open('a') as log:
            result = subprocess.run(leased, stdout=log, stderr=subprocess.STDOUT)
        if result.returncode != 75: break
        time.sleep(1)
    if result.returncode: raise SystemExit('Preparation failed '+name+' exit='+str(result.returncode))
    marker.write_text(utc()+'\n')
    print('Finished',name,utc(),flush=True)
    time.sleep(10)


# Every new harness must compile BDN-generated programs, then prove setup and inventories.
py = ['python3',folder/'prepare.py']
batch('checkout',py+['checkout'])
for name in inputs['revisions']:
    batch('primary-'+name,py+['primary',name])
for name in inputs['component_revisions']:
    batch('component-'+name,py+['component',name])
for name in inputs['revisions']:
    batch('native-'+name,['dotnet',folder/'native-runner/bin/Release/net10.0/Runner.dll',
          folder/'checkouts'/name,*py,'native',name])
batch('inventory',py+['inventory'])
print('All generated BDN programs compile; full setup and compiled inventories prepared. No timing dispatch.',utc(),flush=True)
