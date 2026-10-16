"""Wait for frozen preparation, then validate all compiled families without timing."""
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
protocol = json.loads((folder/'timing-protocol.json').read_text())
assert protocol['image'] == image
protocol_sha = hashlib.sha256((folder/'timing-protocol.json').read_bytes()).hexdigest()
for relative in ('guid-final-v4/validate-all.py','guid-final-v4/validate-timing.py','guid-final-v4/timing-worker.py'):
    assert hashlib.sha256((root/relative).read_bytes()).hexdigest() == protocol['helpers_sha256'][relative]
for name in ('logs', 'completed'): (folder/name).mkdir(exist_ok=True)
uid = str(os.getuid()); gid = str(os.getgid()); docker_gid = str(os.stat('/var/run/docker.sock').st_gid)


def utc(): return datetime.datetime.now(datetime.timezone.utc).isoformat()


def batch(name, command):
    marker = folder/'completed'/name
    if marker.exists(): return
    label = owner+'-guid-input-validation-v4-'+name
    container = ['docker','run','--rm','--init','--network','host','--pid','host','--user',uid+':'+gid,
        '--group-add',docker_gid,'--label','methodcache-design-bench='+label,
        '--volume',str(root)+':'+str(root),'--volume',str(root/'nuget')+':/nuget',
        '--volume',str(root/'home')+':/tmp/home','--volume','/var/run/docker.sock:/var/run/docker.sock',
        '--workdir',str(folder),'--env','TESTCONTAINERS_HOST_OVERRIDE=localhost',
        '--env','TESTCONTAINERS_RYUK_DISABLED=true','--env','METHODCACHE_BENCH_LABEL='+label,'--env','METHODCACHE_BENCH_BACKEND_RECEIPT='+str(folder/'native-image-setup.json'),image,*map(str,command)]
    leased = ['flock','--exclusive','--nonblock','--conflict-exit-code','75',lock,
              'python3',str(root/'leased-batch.py'),label,*container]
    print('Waiting for exclusive capacity:',name,utc(),flush=True)
    while True:
        with (folder/'logs'/('timing-'+name+'.log')).open('a') as log:
            result = subprocess.run(leased, stdout=log, stderr=subprocess.STDOUT)
        if result.returncode != 75: break
        time.sleep(1)
    if result.returncode: raise SystemExit('Preparation failed '+name+' exit='+str(result.returncode))
    marker.write_text(utc()+'\n')
    print('Finished',name,utc(),flush=True)
    time.sleep(10)


# Preparation remains immutable and runs under its existing controller.
parent_pid = 3048867
parent_script = str(folder/'prepare-all.py').encode()
while True:
    try:
        proc = Path('/proc')/str(parent_pid)
        live = proc.stat().st_uid == os.getuid() and parent_script in proc.joinpath('cmdline').read_bytes().split(b'\0')
    except (FileNotFoundError, ProcessLookupError): live = False
    if not live: break
    time.sleep(5)
assert (folder/'completed/inventory').is_file(), 'Preparation failed or is incomplete'
assert hashlib.sha256((folder/'timing-protocol.json').read_bytes()).hexdigest() == protocol_sha
batch('all-timing-inputs',['python3',folder/'validate-timing.py'])
assert (folder/'timing-inputs-validation.json').is_file()
print('All compiled timing inputs validated; no measurement dispatcher started.',utc(),flush=True)
