"""Explicitly launched full GUID timing, after preparation and input review."""
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
protocol = json.loads((folder/'timing-protocol.json').read_text())
assert (root/'OWNER').read_text().strip() == owner
assert protocol['image'] == image
assert (folder/'completed/inventory').is_file(), 'Complete all setup/native/inventory gates before timing'
assert protocol['revisions'] == json.loads((folder/'revisions.json').read_text())
for relative, expected in protocol['helpers_sha256'].items():
    assert hashlib.sha256((root/relative).read_bytes()).hexdigest() == expected
assert sorted(os.sched_getaffinity(0)) == protocol['full_workload_cpus']
prepared = json.loads((folder/'prepared-manifest.json').read_text())
assert {name:source['runtime_sha'] for name,source in prepared['sources'].items()} == protocol['revisions']
assert len(protocol['revisions']) == 14 and set(prepared['components']) == {'pr5','prefix','constructor-base','constructor','siphash','final'}
assert {group:len(cases) for group,cases in prepared['inventories'].items()} == {'local-contract':27,'local-pressure':20,'native':16}
assert all({group:len(cases) for group,cases in item['inventories'].items()} ==
    {'component-lookup':18,'component-facade':36,'component-generated':48} for item in prepared['components'].values())
assert prepared['workloads_sha'] == protocol['workloads_sha']
assert prepared['component_workloads_sha'] == protocol['component_workloads_sha']
assert prepared['native_images'] == protocol['native_images']
for relative, expected in prepared['native_runner_binaries_sha256'].items():
    assert hashlib.sha256((folder/relative).read_bytes()).hexdigest() == expected
validation = json.loads((folder/'timing-inputs-validation.json').read_text())
assert validation['protocol_sha256'] == hashlib.sha256((folder/'timing-protocol.json').read_bytes()).hexdigest()
assert validation['prepared_manifest_sha256'] == hashlib.sha256((folder/'prepared-manifest.json').read_bytes()).hexdigest()
expected_families = {(name,group) for name in protocol['revisions']
                    for group in ('local-contract','local-pressure','native')}
expected_families |= {(name,group) for name in prepared['components']
                     for group in ('component-lookup','component-facade','component-generated')}
assert len(validation['validated']) == 60 and len(expected_families) == 60
assert {(row['revision'],row['group']) for row in validation['validated'] if row['exit']==0} == expected_families
output = folder/'timing-results'
assert not output.exists(), 'Fresh fixed timing series only; preserve every previous attempt'
output.mkdir()
uid = str(os.getuid()); gid = str(os.getgid()); docker_gid = str(os.stat('/var/run/docker.sock').st_gid)


def utc(): return datetime.datetime.now(datetime.timezone.utc).isoformat()


def batch(name, command):
    label = owner+'-guid-timing-v5-'+name
    container = ['docker','run','--rm','--init','--network','host','--pid','host','--user',uid+':'+gid,
        '--group-add',docker_gid,'--label','methodcache-design-bench='+label,
        '--volume',str(root)+':'+str(root),'--volume',str(root/'nuget')+':/nuget',
        '--volume',str(root/'home')+':/tmp/home','--volume','/var/run/docker.sock:/var/run/docker.sock',
        '--workdir',str(folder),'--env','TESTCONTAINERS_HOST_OVERRIDE=localhost',
        '--env','TESTCONTAINERS_RYUK_DISABLED=true','--env','METHODCACHE_BENCH_LABEL='+label,
        '--env','METHODCACHE_BENCH_BACKEND_RECEIPT='+str(output/(name+'-backends.json')),image,*map(str,command)]
    leased = ['flock','--exclusive','--nonblock','--conflict-exit-code','75',lock,
              'python3',str(root/'leased-batch.py'),label,*container]
    print('Waiting for exclusive capacity:',name,utc(),flush=True)
    while True:
        with (output/(name+'-lease.log')).open('a') as log:
            result = subprocess.run(leased,stdout=log,stderr=subprocess.STDOUT)
        if result.returncode != 75: break
        time.sleep(1)
    print('Finished',name,'exit',result.returncode,utc(),flush=True)
    if result.returncode: raise SystemExit('Timing batch failed; preserve this attempt: '+name)
    time.sleep(10)


manifest = {'purpose':'Each actual PR merge-base comparison and cumulative f438 baseline, GUID workloads; components retain separately labelled int/GUID cases',
    'started':utc(),'protocol':protocol,'protocol_sha256':hashlib.sha256((folder/'timing-protocol.json').read_bytes()).hexdigest(),
    'prepared_manifest_sha256':hashlib.sha256((folder/'prepared-manifest.json').read_bytes()).hexdigest(),
    'image':image,'cpu':subprocess.check_output(['lscpu'],text=True),'sdk':(folder/'sdk-info.txt').read_text(),
    'gc':'concurrent workstation, identical pinned .NET 10 image',
    'order':'descending, ascending, descending: three independent launches per revision/family',
    'affinity':'Full workloads inherit all 16 CPUs before CLR startup, preserving contention; component-only jobs inherit CPU2 before CLR startup. Each actual host and benchmark child must be observed with the declared OS affinity.',
    'backend':'Native families start fresh Redis/SQL containers per revision/family launch using frozen repository digests; actual container image IDs/owned labels are checked before and after each launch. No native timing pooled with local nanosecond cases.',
    'new_work':'Before-PR4 RoundTrip labels retain old behavior; KeepCookies before HTTP represents an uncached client. Classify additions separately.',
    'sources':prepared['sources'],'workload_inventories':prepared['inventories'],'components':prepared['components'],
    'comparisons':protocol['comparisons'],'native_images':prepared['native_images'],
    'primary_workloads_sha':prepared['workloads_sha'],'component_workloads_sha':prepared['component_workloads_sha'],'runs':[]}
metadata = output/'metadata.json'
metadata.write_text(json.dumps(manifest,indent=2)+'\n')
revisions = list(protocol['revisions'])
for round_number, order in enumerate([list(reversed(revisions)),revisions,list(reversed(revisions))],1):
    for name in order:
        groups = ['local-contract','local-pressure','native']
        if name in prepared['components']: groups.extend(['component-lookup','component-facade','component-generated'])
        for group in groups:
            assert hashlib.sha256((folder/'prepared-manifest.json').read_bytes()).hexdigest() == manifest['prepared_manifest_sha256']
            assert hashlib.sha256((folder/'timing-protocol.json').read_bytes()).hexdigest() == manifest['protocol_sha256']
            for relative, expected in protocol['helpers_sha256'].items():
                assert hashlib.sha256((root/relative).read_bytes()).hexdigest() == expected
            command = ['python3',folder/'timing-worker.py',str(round_number),name,group]
            if group == 'native':
                command = ['dotnet',folder/'native-runner/bin/Release/net10.0/Runner.dll',folder/'checkouts'/name,*command]
            started = utc()
            batch(f'round-{round_number}-{name}-{group}',command)
            if group == 'native':
                receipt = json.loads((output/f'round-{round_number}-{name}-{group}-backends.json').read_text())
                assert receipt['verified_before'] and receipt['verified_after'] and receipt['child_exit'] == 0
                assert receipt['images'] == prepared['native_images']
            manifest['runs'].append({'round':round_number,'revision':name,'group':group,'started':started,'finished':utc(),'exit':0})
            metadata.write_text(json.dumps(manifest,indent=2)+'\n')
assert len(manifest['runs']) == 180
print('Completed 126 full-workload and 54 component-family launches. Statistical review and feature-equivalence assessment remain required.',utc(),flush=True)
