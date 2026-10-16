"""Reuse immutable fourteen-source primary preparation; add genuine generated components."""
from pathlib import Path
import hashlib
import json
import subprocess
import sys

folder = Path(__file__).resolve().parent
root = folder.parent
inputs = json.loads((folder/'frozen-inputs.json').read_text())
parent = root/inputs['parent_folder']
revisions = inputs['revisions']


def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def run(*command, cwd=None): subprocess.run([str(x) for x in command],cwd=cwd,check=True)


def parent_snapshot():
    assert (parent/'completed/inventory').is_file(), 'Fourteen-source preparation is incomplete'
    assert digest(parent/'frozen-inputs.json') == inputs['parent_frozen_inputs_sha256']
    raw = (parent/'prepared-manifest.json').read_bytes()
    frozen = folder/'parent-prepared-manifest.json'
    if frozen.exists(): assert frozen.read_bytes() == raw, 'Parent manifest changed'
    prepared = json.loads(raw)
    assert {name:data['runtime_sha'] for name,data in prepared['sources'].items()} == revisions
    assert prepared['workloads_sha'] == inputs['primary_workloads_sha']
    return raw, prepared


def runtime(name): return folder/'checkouts'/name
def helpers(): return folder/'workloads/benchmarks/MethodCache.Benchmarks/validation'


component_filters = {
    'component-lookup':['*Microbenchmarks.KeyHashLookupBenchmarks.*'],
    'component-facade':['*Microbenchmarks.PrefixHitPathBenchmarks.*','*Microbenchmarks.MemoryCacheLookupReferenceBenchmarks.*'],
    'component-generated':['*Microbenchmarks.GeneratedPrefixHitPathBenchmarks.*']
}

phase = sys.argv[1]
if phase == 'checkout':
    raw, prepared = parent_snapshot()
    assert digest(folder/'source.bundle') == inputs['source_bundle_sha256']
    assert not (folder/'source').exists(), 'Fresh preparation only'
    run('git','clone','--no-checkout',folder/'source.bundle',folder/'source')
    run('git','worktree','add','--detach',folder/'workloads',inputs['component_workloads_sha'],cwd=folder/'source')
    (folder/'parent-prepared-manifest.json').write_bytes(raw)
    for directory in ('checkouts','harness','component-harness'):(folder/directory).mkdir(exist_ok=True)
    for name,sha in revisions.items():
        for directory in ('checkouts','harness'):
            target = parent/directory/name
            assert target.resolve().is_relative_to(root)
            (folder/directory/name).symlink_to(target,target_is_directory=True)
        assert subprocess.check_output(['git','rev-parse','HEAD'],cwd=runtime(name),text=True).strip() == sha
    for relative,sha in inputs['component_source_sha256'].items():
        assert digest(folder/'workloads/benchmarks/MethodCache.Benchmarks'/relative) == sha
elif phase == 'runner':
    run('dotnet','build',folder/'native-runner/Runner.csproj','-c','Release','--nologo','-v','minimal')
    assert digest(folder/'native-runner/bin/Release/net10.0/backend-images.json') == digest(folder/'native-runner/backend-images.json')
elif phase == 'component':
    name = sys.argv[2]
    assert name in inputs['component_revisions']
    output = folder/'component-harness'/name
    assert not output.exists(), 'Preserve previous attempts'
    run('python3',helpers()/'create-prefix-harness.py','--runtime-root',runtime(name),'--workloads-root',folder/'workloads','--output',output)
    run('dotnet','build',output/'PrefixValidation.csproj','-c','Release','--nologo','-v','minimal')
    run('dotnet',output/'bin/Release/net10.0/MethodCache.Benchmarks.dll','--verify-components',cwd=output)
elif phase == 'native':
    # The outer batch executes this phase through the new digest-pinned runner.
    run('dotnet',folder/'harness/final/bin/Release/net10.0/DesignValidation.dll','--verify-native',cwd=folder/'harness/final')
elif phase == 'inventory':
    _, prepared = parent_snapshot()
    sys.path.insert(0,str(helpers()))
    from workload_inventory import require_same_inventory, normalize_inventory
    for name in revisions:
        path = folder/'harness'/name
        assert json.loads((path/'source-manifest.json').read_text()) == prepared['sources'][name]
        for relative,sha in prepared['binaries_sha256'][name].items():assert digest(path/relative) == sha
    components = {}
    source = None
    program = None
    common = None
    for name in inputs['component_revisions']:
        path = folder/'component-harness'/name
        manifest = json.loads((path/'source-manifest.json').read_text())
        assert manifest['runtime_sha'] == revisions[name] and manifest['workload_sha'] == inputs['component_workloads_sha']
        if source is None:source = manifest['source_sha256'];program = manifest['harness_program_sha256']
        assert source == manifest['source_sha256'] and program == manifest['harness_program_sha256']
        catalogs = {}
        for group,filters in component_filters.items():
            data = json.loads(subprocess.check_output(['dotnet',path/'bin/Release/net10.0/MethodCache.Benchmarks.dll','--workload-inventory',*filters],cwd=path,text=True))
            catalogs[group] = normalize_inventory(data)
        assert {group:len(cases) for group,cases in catalogs.items()} == {'component-lookup':18,'component-facade':36,'component-generated':48}
        names = [case['full_name'] for cases in catalogs.values() for case in cases]
        assert len(set(names)) == 102
        all_cases = json.loads(subprocess.check_output(['dotnet',path/'bin/Release/net10.0/MethodCache.Benchmarks.dll','--workload-inventory','*'],cwd=path,text=True))
        assert set(names) == {case['full_name'] for case in all_cases}
        if common is None:common = catalogs
        for group in catalogs:require_same_inventory(catalogs[group],common[group])
        components[name] = {'source':manifest,'inventories':catalogs,'filters':component_filters,
            'binaries_sha256':{str(p.relative_to(path)):digest(p) for p in sorted((path/'bin/Release/net10.0').rglob('*.dll'))}}
    receipt = json.loads((folder/'native-image-setup.json').read_text())
    assert receipt['verified_before'] and receipt['verified_after'] and receipt['child_exit'] == 0
    assert receipt['images'] == json.loads((folder/'native-runner/backend-images.json').read_text())
    prepared['components'] = components
    prepared['component_workloads_sha'] = inputs['component_workloads_sha']
    prepared['native_images'] = receipt['images']
    prepared['native_runner_dll_sha256'] = digest(folder/'native-runner/bin/Release/net10.0/Runner.dll')
    prepared['native_runner_binaries_sha256'] = {str(p.relative_to(folder)):digest(p)
        for p in sorted((folder/'native-runner/bin/Release/net10.0').rglob('*.dll'))}
    prepared['native_image_setup'] = receipt
    prepared['measurement_not_started'] = True
    (folder/'sdk-info.txt').write_text(subprocess.check_output(['dotnet','--info'],text=True))
    (folder/'prepared-manifest.json').write_text(json.dumps(prepared,indent=2)+'\n')
    print('Prepared fourteen primary GUID points and six 102-case component points; pinned native images verified. No timings started.')
else:raise SystemExit('Unknown phase')
