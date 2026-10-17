from pathlib import Path
import hashlib,json,os,subprocess,datetime
f=Path.home()/'methodcache-design-bench-20261015/guid-final-v4';root=f.parent
h=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
assert h(f/'timing-protocol.json')=='047c51a87c0437a40d005bf11b66b226db6cd6114bd170f45fc1a6acc705ba60'
p=json.loads((f/'prepared-manifest.json').read_text());t=json.loads((f/'timing-protocol.json').read_text());v=json.loads((f/'timing-inputs-validation.json').read_text())
assert (root/'OWNER').read_text().strip()=='methodcache-design-20261015-01a13a93'
assert p['measurement_not_started'] and v['measurement_started'] is False and not (f/'timing-results').exists()
assert (f/'completed/all-timing-inputs').is_file() and (f/'completed/inventory').is_file()
assert v['protocol_sha256']==h(f/'timing-protocol.json') and v['prepared_manifest_sha256']==h(f/'prepared-manifest.json')
assert len(p['sources'])==14 and {k:x['runtime_sha'] for k,x in p['sources'].items()}==t['revisions']
expected={(n,g) for n in p['sources'] for g in p['inventories']}|{(n,g) for n,x in p['components'].items() for g in x['inventories']}
assert len(expected)==len(v['validated'])==60 and {(r['revision'],r['group']) for r in v['validated'] if r['exit']==0}==expected
assert {g:len(c) for g,c in p['inventories'].items()}=={'local-contract':27,'local-pressure':20,'native':16}
assert len(p['components'])==6 and all({g:len(c) for g,c in x['inventories'].items()}=={'component-lookup':18,'component-facade':36,'component-generated':48} for x in p['components'].values())
assert p['workloads_sha']==t['workloads_sha'] and p['component_workloads_sha']==t['component_workloads_sha']
assert all(h(root/k)==value for k,value in t['helpers_sha256'].items())
assert all(h(f/k)==value for k,value in p['native_runner_binaries_sha256'].items())
receipt=json.loads((f/'native-image-setup.json').read_text());assert receipt==p['native_image_setup'] and receipt['images']==p['native_images']==t['native_images'] and receipt['verified_before'] and receipt['verified_after'] and receipt['child_exit']==0
files=0
for directory,items in [('harness',{n:{'source':x,'binaries_sha256':p['binaries_sha256'][n]} for n,x in p['sources'].items()}),('component-harness',p['components'])]:
 for name,item in items.items():
  harness=f/directory/name;s=item['source'];assert json.loads((harness/'source-manifest.json').read_text())==s
  assert subprocess.check_output(['git','rev-parse','HEAD'],cwd=s['runtime_root'],text=True).strip()==s['runtime_sha']
  assert not subprocess.check_output(['git','diff','--name-only','HEAD','--','src','benchmarks'],cwd=s['runtime_root'],text=True).strip()
  assert h(harness/'Program.cs')==s['harness_program_sha256']
  for relative,digest in s['source_sha256'].items():assert h(harness/Path(relative).name)==digest
  for relative,digest in item['binaries_sha256'].items():assert h(harness/relative)==digest;files+=1
assert sorted(os.sched_getaffinity(0))==t['full_workload_cpus']==list(range(16)) and t['component_cpus']==[2]
assert t['warmups']==16 and t['actual_iterations']==12 and t['iteration_ms']==500
assert len(t['comparisons'])==11
review={'at_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'protocol_sha256':h(f/'timing-protocol.json'),'prepared_manifest_sha256':h(f/'prepared-manifest.json'),'validation_sha256':h(f/'timing-inputs-validation.json'),'runtime_points':14,'component_points':6,'validated_families':60,'dll_files_rehashed':files,'primary_cases':63,'component_cases':102,'planned_launches':180,'comparison_edges':11,'native_images_verified_before_and_after':True,'measurement_started':False}
print(json.dumps(review,indent=2));(f/'preflight-independent-review.json').write_text(json.dumps(review,indent=2)+'\n')
