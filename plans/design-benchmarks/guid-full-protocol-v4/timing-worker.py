"""Bounded worker for one immutable, inventory-checked GUID family launch."""
from pathlib import Path
import datetime
import hashlib
import json
import os
import re
import subprocess
import sys
import time

folder = Path(__file__).resolve().parent
root = folder.parent
sys.path.insert(0, str(root))
from process_activity import ActivityMonitor, parse_snapshot
from bdn_affinity import owned_dotnet_affinities

protocol_bytes = (folder/'timing-protocol.json').read_bytes()
prepared_bytes = (folder/'prepared-manifest.json').read_bytes()
protocol = json.loads(protocol_bytes)
prepared = json.loads(prepared_bytes)
if sys.argv[1] == '--validate-only':
    # Preflight runs before a timing directory exists. Its parent freezes and rechecks both raw files.
    metadata = {'protocol_sha256':hashlib.sha256(protocol_bytes).hexdigest(),
                'prepared_manifest_sha256':hashlib.sha256(prepared_bytes).hexdigest()}
else:
    metadata = json.loads((folder/'timing-results/metadata.json').read_text())
sys.path.insert(0, str(folder/'checkouts/final/benchmarks/MethodCache.Benchmarks/validation'))
from workload_inventory import normalize_inventory, require_same_inventory, verify_reports


def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def utc(): return datetime.datetime.now(datetime.timezone.utc).isoformat()


def frozen_protocol():
    if digest(folder/'timing-protocol.json') != metadata['protocol_sha256']:
        raise RuntimeError('Timing protocol changed after initialization')
    if digest(folder/'prepared-manifest.json') != metadata['prepared_manifest_sha256']:
        raise RuntimeError('Prepared manifest changed after timing initialization')
    for relative, expected in protocol['helpers_sha256'].items():
        if digest(root/relative) != expected: raise RuntimeError('Frozen timing helper changed: '+relative)
    for relative, expected in prepared.get('native_runner_binaries_sha256',{}).items():
        if digest(folder/relative) != expected: raise RuntimeError('Pinned native runner dependency changed: '+relative)


def inputs(name, group):
    frozen_protocol()
    component = group.startswith('component-')
    item = prepared['components'][name] if component else prepared
    harness = folder/('component-harness' if component else 'harness')/name
    source = item['source'] if component else item['sources'][name]
    expected = item['inventories'][group]
    binaries = item['binaries_sha256'] if component else item['binaries_sha256'][name]
    manifest = json.loads((harness/'source-manifest.json').read_text())
    if manifest != source: raise RuntimeError('Harness source manifest changed')
    runtime = Path(source['runtime_root'])
    head = subprocess.check_output(['git','rev-parse','HEAD'],cwd=runtime,text=True).strip()
    if head != source['runtime_sha']: raise RuntimeError('Runtime checkout moved')
    dirty = subprocess.check_output(['git','diff','--name-only','HEAD','--','src','benchmarks'],cwd=runtime,text=True)
    if dirty.strip(): raise RuntimeError('Runtime checkout has modified sources')
    for relative, expected_digest in source['source_sha256'].items():
        if digest(harness/Path(relative).name) != expected_digest: raise RuntimeError('Workload bytes changed')
    if digest(harness/'Program.cs') != source['harness_program_sha256']: raise RuntimeError('Harness program changed')
    for relative, expected_digest in binaries.items():
        if digest(harness/relative) != expected_digest: raise RuntimeError('Compiled DLL changed: '+relative)
    executable = harness/'bin/Release/net10.0'/('MethodCache.Benchmarks.dll' if component else 'DesignValidation.dll')
    filters = item['filters'][group]
    catalog = json.loads(subprocess.check_output(['dotnet',executable,'--workload-inventory',*filters],cwd=harness,text=True))
    require_same_inventory(catalog, expected)
    return harness, executable, filters, normalize_inventory(expected)


def run():
    round_number, name, group = sys.argv[1:4]
    harness, executable, filters, expected = inputs(name, group)
    cpus = protocol['component_cpus'] if group.startswith('component-') else protocol['full_workload_cpus']
    output = folder/'timing-results'
    stem = f'round-{round_number}-{name}-{group}'
    if group == 'native':
        receipt = json.loads((output/(stem+'-backends.json')).read_text())
        if not receipt['verified_before'] or receipt['images'] != prepared['native_images']:
            raise RuntimeError('Native containers lack frozen image evidence before timing')
    target = output/stem
    if target.exists(): raise RuntimeError('Preserve previous attempts; never overwrite timing evidence')
    monitor = ActivityMonitor()

    def snapshot():
        return subprocess.check_output(['ps','-Ao','pid=,ppid=,stat=,pcpu=,time=,etime=,command='],text=True)

    # Every batch also gets a quiet interval after native-container startup.
    quiet_since = time.monotonic()
    while time.monotonic() - quiet_since < protocol['quiet_seconds']:
        if monitor.observe(snapshot()): quiet_since = time.monotonic()
        time.sleep(2)
    command = ['taskset','--cpu-list',','.join(map(str,cpus)),'dotnet',str(executable),
        '--filter',*filters,'--warmupCount',str(protocol['warmups']),
        '--iterationCount',str(protocol['actual_iterations']), '--iterationTime',str(protocol['iteration_ms']),
        '--launchCount','1','--exporters','json','--stopOnFirstError','--artifacts',str(target)]
    interference = []
    observed = {}
    log_path = output/(stem+'.log')
    with log_path.open('x') as log:
        child = subprocess.Popen(command,cwd=harness,stdout=log,stderr=subprocess.STDOUT)
        while child.poll() is None:
            current = snapshot()
            active = monitor.observe(current, child.pid)
            if active: interference.append({'at':utc(),'processes':active})
            for pid, allowed in owned_dotnet_affinities(parse_snapshot(current),child.pid,os.sched_getaffinity).items():
                row = observed.setdefault(str(pid),{'allowed_cpus':allowed,'observations':0,'mismatch':False})
                row['observations'] += 1
                row['mismatch'] = row['mismatch'] or allowed != cpus
            time.sleep(2)
    (output/(stem+'-interference.json')).write_text(json.dumps(interference,indent=2)+'\n')
    text = log_path.read_text()
    benchmark_pids = re.findall(r'// Benchmark Process (\d+) has exited with code 0\.',text)
    affinity = {'root_pid':child.pid,'benchmark_pids':benchmark_pids,'required_cpus':cpus,'processes':observed}
    (output/(stem+'-affinity.json')).write_text(json.dumps(affinity,indent=2)+'\n')
    if child.returncode: raise SystemExit(child.returncode)
    if interference: raise RuntimeError('External build/test/publish overlapped timing')
    if 'Failed to set up processor affinity' in text: raise RuntimeError('Failed BDN affinity change')
    if text.count('// GC=Concurrent Workstation') != len(expected): raise RuntimeError('Unexpected workload GC configuration')
    required = {str(child.pid), *benchmark_pids}
    if len(set(benchmark_pids)) != len(expected) or not required.issubset(observed):
        raise RuntimeError('Missing actual OS affinity samples for workload host/children')
    if any(row['allowed_cpus'] != cpus or row['mismatch'] for row in observed.values()):
        raise RuntimeError('An owned CLR process ran with unexpected CPU affinity')
    reports = list((target/'results').glob('*-report-full*.json'))
    for report in reports:
        environment = json.loads(report.read_text())['HostEnvironmentInfo']
        if environment['DotNetCliVersion'] != protocol['sdk_version'] or not environment['RuntimeVersion'].startswith(protocol['runtime_prefix']):
            raise RuntimeError('Unexpected workload SDK/runtime')
    count = verify_reports(reports,expected,protocol['actual_iterations'])
    inputs(name,group)
    print('Verified complete measured cases:',count,utc(),flush=True)


if __name__ == '__main__':
    if sys.argv[1] == '--validate-only':
        _, _, _, expected = inputs(sys.argv[2],sys.argv[3])
        print('Validated immutable inputs and actual inventory:',len(expected))
    else: run()
