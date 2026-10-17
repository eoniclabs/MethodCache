"""Resume immutable V5 workers with a separately frozen, source-scoped schedule."""
from pathlib import Path
import argparse
import datetime
import hashlib
import importlib.util
import json
import os
import re
import subprocess
import time

folder = Path(__file__).resolve().parent
root = folder.parent
previous = root/'guid-final-v5'
owner = 'methodcache-design-20261015-01a13a93'


def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def utc(): return datetime.datetime.now(datetime.timezone.utc).isoformat()


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


inventory = load_module('focused_inventory', previous/'workload_inventory.py')
selection_path = folder/'selection.json'
selection = json.loads(selection_path.read_text())
protocol = json.loads((previous/'timing-protocol.json').read_text())
prepared = json.loads((previous/'prepared-manifest.json').read_text())
original = json.loads((previous/'timing-results/metadata.json').read_text())
output = previous/'timing-results'
selection_hash = digest(selection_path)
protocol_hash = digest(previous/'timing-protocol.json')
prepared_hash = digest(previous/'prepared-manifest.json')


def frozen():
    assert (root/'OWNER').read_text().strip() == owner
    assert digest(selection_path) == selection_hash
    assert digest(previous/'timing-protocol.json') == protocol_hash == selection['measurement_protocol_sha256'] == original['protocol_sha256']
    assert digest(previous/'prepared-manifest.json') == prepared_hash == original['prepared_manifest_sha256']
    assert selection['revisions'] == protocol['revisions']
    for relative, expected in protocol['helpers_sha256'].items():
        assert digest(root/relative) == expected, relative


def old_controller_live():
    pid = int((previous/'timing-driver.pid').read_text().strip())
    path = Path(f'/proc/{pid}/cmdline')
    return path.exists() and str(previous/'timing-all.py').encode() in path.read_bytes()


def key(row): return row['round'], row['revision'], row['group']
def stem(row): return f"round-{row['round']}-{row['revision']}-{row['group']}"


def verify(row):
    name, group = row['revision'], row['group']
    prefix = stem(row)
    catalog = prepared['components'][name]['inventories'] if group.startswith('component-') else prepared['inventories']
    expected = inventory.normalize_inventory(catalog[group])
    reports = sorted((output/prefix/'results').glob('*-report-full*.json'))
    assert inventory.verify_reports(reports, expected, protocol['actual_iterations']) == selection['counts'][group]
    assert json.loads((output/(prefix+'-interference.json')).read_text()) == []
    affinity = json.loads((output/(prefix+'-affinity.json')).read_text())
    cpus = protocol['component_cpus'] if group.startswith('component-') else protocol['full_workload_cpus']
    processes = affinity['processes']
    required = {str(affinity['root_pid']), *affinity['benchmark_pids']}
    assert affinity['required_cpus'] == cpus and len(set(affinity['benchmark_pids'])) == len(expected)
    assert required.issubset(processes)
    assert all(p['allowed_cpus'] == cpus and p['observations'] > 0 and not p['mismatch'] for p in processes.values())
    log = (output/(prefix+'.log')).read_text()
    assert log.count('// GC=Concurrent Workstation') == len(expected)
    assert 'Failed to set up processor affinity' not in log
    for report in reports:
        environment = json.loads(report.read_text())['HostEnvironmentInfo']
        assert environment['DotNetCliVersion'] == protocol['sdk_version']
        assert environment['RuntimeVersion'].startswith(protocol['runtime_prefix'])
    if group == 'native':
        receipt = json.loads((output/(prefix+'-backends.json')).read_text())
        assert receipt['verified_before'] and receipt['verified_after'] and receipt['child_exit'] == 0
        assert receipt['images'] == prepared['native_images']


def imported():
    # The old driver sleeps after a successful worker and before adding its metadata
    # row. Its immutable Finished/exit=0 receipt covers that last idle-boundary row.
    completed = {key(row): dict(row) for row in original['runs']}
    assert len(completed) == len(original['runs'])
    starts = {}
    pattern = r'round-(\d+)-(.+)-(local-contract|local-pressure|native|component-lookup|component-facade|component-generated)'
    for line in (previous/'timing-driver.log').read_text().splitlines():
        match = re.fullmatch('Waiting for exclusive capacity: ('+pattern+r') (.+)', line)
        if match:
            starts[match[1]] = match[5]
            continue
        match = re.fullmatch('Finished ('+pattern+r') exit (\d+) (.+)', line)
        if not match:
            continue
        assert match[5] == '0', 'Prior timing batch failed; investigate rather than skipping it'
        row = {'round':int(match[2]), 'revision':match[3], 'group':match[4],
               'started':starts[match[1]], 'finished':match[6], 'exit':0}
        completed.setdefault(key(row), row)
    for row in completed.values():
        verify(row)
    selected, diagnostic = [], []
    for row in completed.values():
        row['origin'] = 'v5'
        (selected if row['group'] in selection['selected_families'][row['revision']] else diagnostic).append(row)
    return selected, diagnostic


def schedule():
    rows = []
    for round_number, name in [(1,'final'),(1,'base'),(2,'base'),(2,'final'),(3,'final'),(3,'base')]:
        rows.extend({'round':round_number,'revision':name,'group':group} for group in selection['selected_families'][name])
    names = list(selection['revisions'])
    for round_number, order in enumerate([names[::-1], names, names[::-1]], 1):
        for name in order:
            if name in {'base','final'}: continue
            rows.extend({'round':round_number,'revision':name,'group':group} for group in selection['selected_families'][name])
    expected = {(r,n,g) for r in range(1,4) for n,groups in selection['selected_families'].items() for g in groups}
    assert len(rows) == len({key(r) for r in rows}) == selection['family_launches']
    assert {key(row) for row in rows} == expected
    return rows


def batch(row):
    name = stem(row)
    assert not (output/name).exists(), 'Never overwrite or rerun an existing attempt'
    label = owner+'-guid-timing-v6-'+name
    uid, gid, docker_gid = map(str, [os.getuid(),os.getgid(),os.stat('/var/run/docker.sock').st_gid])
    command = ['python3',str(previous/'timing-worker.py'),str(row['round']),row['revision'],row['group']]
    if row['group'] == 'native':
        command = ['dotnet',str(previous/'native-runner/bin/Release/net10.0/Runner.dll'),str(previous/'checkouts'/row['revision']),*command]
    container = ['docker','run','--rm','--init','--network','host','--pid','host','--user',uid+':'+gid,
        '--group-add',docker_gid,'--label','methodcache-design-bench='+label,
        '--volume',str(root)+':'+str(root),'--volume',str(root/'nuget')+':/nuget',
        '--volume',str(root/'home')+':/tmp/home','--volume','/var/run/docker.sock:/var/run/docker.sock',
        '--workdir',str(previous),'--env','TESTCONTAINERS_HOST_OVERRIDE=localhost',
        '--env','TESTCONTAINERS_RYUK_DISABLED=true','--env','METHODCACHE_BENCH_LABEL='+label,
        '--env','METHODCACHE_BENCH_BACKEND_RECEIPT='+str(output/(name+'-backends.json')),protocol['image'],*command]
    leased = ['flock','--exclusive','--nonblock','--conflict-exit-code','75','/srv/skribera-ci/ci-build-capacity.lock',
              'python3',str(root/'leased-batch.py'),label,*container]
    print('Waiting for exclusive capacity:', name, utc(), flush=True)
    while True:
        frozen()
        assert not old_controller_live(), 'Original driver resumed; stop before competing schedules'
        with (output/(name+'-lease.log')).open('a') as log:
            result = subprocess.run(leased, stdout=log, stderr=subprocess.STDOUT)
        if result.returncode != 75: break
        time.sleep(1)
    print('Finished',name,'exit',result.returncode,utc(),flush=True)
    if result.returncode: raise SystemExit('Preserve failed attempt: '+name)
    verify(row)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run', action='store_true')
    args = parser.parse_args()
    frozen()
    rows = schedule()
    selected, diagnostic = imported()
    print('Validated selection:',len(rows),'launches;',len(selected),'reusable;',len(diagnostic),'preserved diagnostics',flush=True)
    if not args.run: return
    assert not old_controller_live(), 'Stop only the owned original driver at an idle boundary first'
    stop_path = folder/'stop-at-idle.json'
    stop = json.loads(stop_path.read_text())
    assert stop['controller_pid'] == int((previous/'timing-driver.pid').read_text().strip())
    assert stop['idle_children'] == [] and stop['last_driver_line'].startswith('Finished ')
    assert stop['original_metadata_sha256'] == digest(previous/'timing-results/metadata.json')
    assert str(previous/'timing-all.py') in stop['controller_command']
    assert sorted(os.sched_getaffinity(0)) == protocol['full_workload_cpus']
    metadata_path = folder/'metadata.json'
    assert not metadata_path.exists(), 'Preserve previous controller attempts'
    metadata = {**original,'purpose':'Focused source-scoped acceptance schedule; original workload settings unchanged',
        'selection':selection,'selection_sha256':selection_hash,'controller_sha256':digest(Path(__file__)),
        'idle_stop_receipt_sha256':digest(stop_path),
        'original_metadata_sha256':digest(previous/'timing-results/metadata.json'),
        'started':utc(),'order':'Cumulative pairs prioritized; other edges descending/ascending/descending. V5 imports retain original execution order.',
        'diagnostic_launches':diagnostic,'runs':selected,'controller_events':[]}
    with metadata_path.open('x') as file:
        file.write(json.dumps(metadata,indent=2)+'\n')
    done = {key(row) for row in selected}
    for row in rows:
        if key(row) in done: continue
        frozen()
        started = utc()
        batch(row)
        metadata['runs'].append({**row,'started':started,'finished':utc(),'exit':0,'origin':'v6'})
        temporary = metadata_path.with_suffix('.tmp')
        temporary.write_text(json.dumps(metadata,indent=2)+'\n')
        temporary.replace(metadata_path)
        done.add(key(row))
        time.sleep(10)
    assert len(metadata['runs']) == len(done) == selection['family_launches']
    print('Completed selected schedule; statistical and feature-equivalence review still required.',utc(),flush=True)


if __name__ == '__main__': main()
