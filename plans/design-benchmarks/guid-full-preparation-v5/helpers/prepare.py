"""Fresh post-V4-failure harnesses; compile real BDN programs before timing."""
from pathlib import Path
import hashlib,json,subprocess,sys
f=Path(__file__).resolve().parent;root=f.parent
inputs=json.loads((f/'frozen-inputs.json').read_text());parent=root/inputs['parent_folder']
h=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
def run(*args,cwd=None):subprocess.run([str(v) for v in args],cwd=cwd,check=True)
def source_helpers():return f/'workloads/benchmarks/MethodCache.Benchmarks/validation'
def dlls(harness,assembly):
 base=harness/'bin/Release/net10.0'
 return {str(p.relative_to(harness)):h(p) for p in sorted(base.rglob('*.dll'))
         if not p.relative_to(base).parts[0].startswith(assembly+'-')}
phase=sys.argv[1]
if phase=='checkout':
 assert (parent/'completed/all-timing-inputs').is_file()
 assert h(parent/'prepared-manifest.json')==inputs['parent_prepared_sha256']
 assert h(f/'source.bundle')==inputs['source_bundle_sha256']
 run('git','clone','--no-checkout',f/'source.bundle',f/'source')
 run('git','worktree','add','--detach',f/'workloads',inputs['primary_workloads_sha'],cwd=f/'source')
 run('git','worktree','add','--detach',f/'component-workloads',inputs['component_workloads_sha'],cwd=f/'source')
 for d in ('checkouts','harness','component-harness'):(f/d).mkdir()
 for name,sha in inputs['revisions'].items():
  (f/'checkouts'/name).symlink_to(parent/'checkouts'/name,target_is_directory=True)
  assert subprocess.check_output(['git','rev-parse','HEAD'],cwd=f/'checkouts'/name,text=True).strip()==sha
 (f/'native-runner').symlink_to(parent/'native-runner',target_is_directory=True)
 for relative,value in inputs['primary_sources_sha256'].items():assert h(f/'workloads/benchmarks/MethodCache.Benchmarks'/relative)==value
 for relative,value in inputs['component_sources_sha256'].items():assert h(f/'component-workloads/benchmarks/MethodCache.Benchmarks'/relative)==value
 run('python3','-m','unittest','discover','-s',source_helpers())
elif phase=='primary':
 name=sys.argv[2];out=f/'harness'/name;assert not out.exists()
 run('python3',source_helpers()/'create-harness.py','--runtime-root',f/'checkouts'/name,'--output',out)
 program=out/'Program.cs';original=program.read_text();anchor='BenchmarkSwitcher.FromTypes(types).Run(args, config);';assert original.count(anchor)==1
 extra=(f/'extra-setup.cs.txt').read_text();program.write_text(original.replace(anchor,extra+anchor))
 mp=out/'source-manifest.json';m=json.loads(mp.read_text());m['canonical_harness_program_sha256']=m['harness_program_sha256'];m['harness_program_sha256']=h(program);m['preparation_extension_sha256']=h(f/'extra-setup.cs.txt');mp.write_text(json.dumps(m,indent=2)+'\n')
 run('dotnet','build',out/'DesignValidation.csproj','-c','Release','--nologo','-v','minimal')
 exe=out/'bin/Release/net10.0/DesignValidation.dll'
 run('dotnet',exe,'--build-only','*',cwd=out)
 run('dotnet',exe,'--verify-generated',cwd=out)
 run('dotnet',exe,'--verify-extra-local',cwd=out)
elif phase=='component':
 name=sys.argv[2];out=f/'component-harness'/name;assert not out.exists()
 run('python3',source_helpers()/'create-prefix-harness.py','--runtime-root',f/'checkouts'/name,'--workloads-root',f/'component-workloads','--output',out)
 run('dotnet','build',out/'MethodCache.Benchmarks.csproj','-c','Release','--nologo','-v','minimal')
 exe=out/'bin/Release/net10.0/MethodCache.Benchmarks.dll'
 run('dotnet',exe,'--build-only','*',cwd=out)
 run('dotnet',exe,'--verify-components',cwd=out)
elif phase=='native':
 name=sys.argv[2];out=f/'harness'/name
 run('dotnet',out/'bin/Release/net10.0/DesignValidation.dll','--verify-native',cwd=out)
elif phase=='inventory':
 sys.path.insert(0,str(source_helpers()))
 from workload_inventory import compiled_inventory,require_same_inventory,normalize_inventory
 old=json.loads((parent/'prepared-manifest.json').read_text());sources={};binaries={};inventories=None;common_sources=None;common_program=None
 for name in inputs['revisions']:
  out=f/'harness'/name;s=json.loads((out/'source-manifest.json').read_text());assert s['runtime_sha']==inputs['revisions'][name] and s['workloads_sha']==inputs['primary_workloads_sha']
  if common_sources is None:common_sources=s['source_sha256'];common_program=s['harness_program_sha256']
  assert s['source_sha256']==common_sources and s['harness_program_sha256']==common_program
  catalog={g:compiled_inventory(out,filters) for g,filters in old['filters'].items()}
  if inventories is None:inventories=catalog
  for g,values in catalog.items():require_same_inventory(values,old['inventories'][g]);require_same_inventory(values,inventories[g])
  sources[name]=s;binaries[name]=dlls(out,'DesignValidation');assert binaries[name].keys()==old['binaries_sha256'][name].keys()
 components={};common_program=None
 for name in old['components']:
  out=f/'component-harness'/name;s=json.loads((out/'source-manifest.json').read_text());prior=old['components'][name]
  assert s['runtime_sha']==inputs['revisions'][name] and s['workload_sha']==inputs['component_workloads_sha'] and s['source_sha256']==prior['source']['source_sha256']
  if common_program is None:common_program=s['harness_program_sha256']
  assert s['harness_program_sha256']==common_program
  catalog={g:normalize_inventory(json.loads(subprocess.check_output(['dotnet',out/'bin/Release/net10.0/MethodCache.Benchmarks.dll','--workload-inventory',*filters],cwd=out,text=True))) for g,filters in prior['filters'].items()}
  for g,values in catalog.items():require_same_inventory(values,prior['inventories'][g])
  b=dlls(out,'MethodCache.Benchmarks');assert b.keys()==prior['binaries_sha256'].keys()
  components[name]={'source':s,'inventories':catalog,'filters':prior['filters'],'binaries_sha256':b}
 receipts={}
 for name in inputs['revisions']:
  r=json.loads((f/('native-'+name+'-backends.json')).read_text());assert r['images']==old['native_images'] and r['verified_before'] and r['verified_after'] and r['child_exit']==0;receipts[name]=r
 prepared={'sources':sources,'inventories':inventories,'binaries_sha256':binaries,'components':components,'filters':old['filters'],'image':inputs['image'],'workloads_sha':inputs['primary_workloads_sha'],'component_workloads_sha':inputs['component_workloads_sha'],'native_images':old['native_images'],'native_runner_dll_sha256':h(f/'native-runner/bin/Release/net10.0/Runner.dll'),'native_runner_binaries_sha256':{k:h(f/k) for k in old['native_runner_binaries_sha256']},'native_image_setup':receipts['final'],'native_setups':receipts,'generated_bdn_programs_compiled':{'primary':14,'components':6},'measurement_not_started':True}
 (f/'native-image-setup.json').write_text(json.dumps(receipts['final'],indent=2)+'\n')
 (f/'sdk-info.txt').write_text(subprocess.check_output(['dotnet','--info'],text=True))
 (f/'prepared-manifest.json').write_text(json.dumps(prepared,indent=2)+'\n')
 print('All 14 primary and 6 component generated programs compile; inventories, setup and pinned native images pass. No timing dispatch.')
else:raise SystemExit('Unknown phase')
