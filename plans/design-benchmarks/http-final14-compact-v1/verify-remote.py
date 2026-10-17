"""Terminal-only independent verification and unchanged-text collection of the fixed current HTTP cohort."""
from pathlib import Path
import datetime,hashlib,json,math,os,tarfile
folder=Path.home()/'methodcache-design-bench-20261015/http-final14-compact-v1';root=folder.parent
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
assert (root/'OWNER').read_text().strip()=='methodcache-design-20261015-01a13a93'
try:os.kill(int((folder/'dispatcher.pid').read_text()),0)
except ProcessLookupError:pass
else:raise RuntimeError('The exact dispatcher is live; do not collect or inspect partial means')
assert (folder/'exit').read_text().strip()=='0'
i=json.loads((folder/'inputs.json').read_text());d=json.loads((folder/'prepared.json').read_text());start=json.loads((folder/'timing-start.json').read_text())
assert len(i['cases'])==len(set(i['cases']))==14 and i['pairs']==10 and i['warmups']==32
assert sha(folder/'inputs.json')==d['inputs_sha256']==start['inputs_sha256']=='1a6616becd5f82bf5fc1868b330f34fbb0fa1d05bbc1a412280bfc44664fad36'
assert sha(folder/'prepared.json')==start['prepared_sha256']
assert d['source_hashes']['base']==d['source_hashes']['head']
for n,h in i['files_sha256'].items():assert sha(folder/n)==h,n
origin=root/'http-compact-telemetry-pilot-v1';assert sha(origin/'prepared.json')==d['origin_prepared_sha256']
equivalence=json.loads((folder/'published-source-equivalence.json').read_text());receipt=json.loads((folder/'published-source-equivalence-verified.json').read_text())
expected={r['path']:r['sha256'] for r in equivalence['sources']}
assert equivalence['published_head']==i['published_source_checkpoint']==receipt['published_head']=='5257fb63fb72393c817d74292e989299ede374d2'
assert len(expected)==receipt['source_files']==224 and expected=={r['path']:r['sha256'] for r in d['production_sources']['head']}
assert receipt['source_catalogue_sha256']==sha(folder/'published-source-equivalence.json') and receipt['prepared_sha256']==sha(folder/'prepared.json')
base=root/'final-paired-cost-v2/base'
for side,tree in [('base',base),('head',folder/'tree')]:
 for row in d['production_sources'][side]:assert sha(tree/row['path'])==row['sha256'],row['path']
 for n,h in d['source_hashes'][side].items():assert sha(tree/n)==h,n
for rows in d['binaries'].values():
 for n,h in rows.items():assert sha(folder/n)==h,n
assert d['binaries']['base/main']['bin/base/main/MethodCache.WarmupProbe.dll']==d['binaries']['head/main']['bin/head/main/MethodCache.WarmupProbe.dll']==d['helper_original_sha256']
# Retain the exact compiled workload/helper source bodies, independently of repository availability.
source_dir=folder/'frozen-harness-source';source_dir.mkdir(exist_ok=True)
source_rows=[]
for n,h in d['source_hashes']['head'].items():
 source=folder/'tree'/n;assert sha(source)==h
 target=source_dir/n;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(source.read_bytes());source_rows.append({'path':n,'sha256':h,'kind':'identical workload on both runtime sides'})
prior=root/'http-cumulative-time32-v1';prior_d=json.loads((prior/'prepared.json').read_text())
assert prior_d['binaries']['base/main']['bin/base/main/MethodCache.WarmupProbe.dll']==d['helper_original_sha256']
for name in ['Program.cs','Profile.csproj']:
 source=prior/'project'/name;target=source_dir/'helper'/name;target.parent.mkdir(exist_ok=True);target.write_bytes(source.read_bytes());source_rows.append({'path':'helper/'+name,'sha256':sha(source),'kind':'original helper source; unchanged compiled helper reused and verified against original preparation'})
(folder/'frozen-harness-source-index.json').write_text(json.dumps({'prepared_sha256':sha(folder/'prepared.json'),'origin_helper_prepared_sha256':sha(prior/'prepared.json'),'files':source_rows},indent=2)+'\n')
records={};seen_cases=[]
for number,group in enumerate(d['groups']):
 g=folder/'results'/f'group-{number:02d}';assert (g/'exit').read_text().strip()=='0' and len(group)==2
 seen_cases.extend(x['case'] for x in group)
 launches=[json.loads(l) for l in (g/'launches.jsonl').read_text().splitlines()]
 expected_order=[(s,n,x['case']) for n in range(1,11) for x in group for s in (['base','head'] if n%2 else ['head','base'])]
 assert [(r['side'],r['pair'],r['case']) for r in launches]==expected_order and len(launches)==40
 observations=json.loads((g/'observations.json').read_text());assert observations and all(not o['external'] for o in observations)
 assert all(cpus and set(cpus).issubset(set(range(16))) for o in observations for cpus in o['thread_affinities'].values())
 for r,key in zip(launches,expected_order):
  path=g/r['output'];assert path.resolve().is_relative_to(g.resolve()) and sha(path)==r['sha256']
  x=json.loads(path.read_text());assert x['case']==r['case'] and key not in records
  assert x['warmupCount']==len(x['warmupMeasurements'])==32 and x['actualIterations']==len(x['actualMeasurements'])==12
  growing=x['case'].startswith(('HttpResponsePathBenchmarks.Miss(','DesignHttpWorkloadBenchmarks.Miss('))
  assert x['growingSetFixedInvocations']==(8192 if growing else None)
  if growing:assert all(m['Operations']==8192 for m in x['actualMeasurements'])
  assert x['runtime']=='.NET 10.0.12' and not x['serverGC'] and x['gcLatencyMode']=='Interactive'
  assert isinstance(x['ns'],(float,int)) and not isinstance(x['ns'],bool) and math.isfinite(x['ns']) and x['ns']>0
  assert all(m['Operations']>0 and math.isfinite(m['Nanoseconds']) and m['Nanoseconds']>0 for m in x['actualMeasurements'])
  assert any(o['thread_affinities'] and r['started_utc']<=o['utc']<=r['finished_utc'] for o in observations)
  for lib in x['libraries']:
   libpath=Path(lib['location']);assert libpath.is_relative_to(folder/'bin'/r['side']/'main') and sha(libpath)==lib['sha256'].lower()
   assert d['binaries'][r['side']+'/main'][str(libpath.relative_to(folder))]==lib['sha256'].lower()
  records[key]=x
assert seen_cases==i['cases'] and len(records)==280 and len(d['groups'])==7
files=[p for p in folder.iterdir() if p.is_file() and p.name not in ['completed-integrity.json','completed-text-only.tar.gz']]
files.extend(p for sub in ['results','frozen-harness-source'] for p in (folder/sub).rglob('*') if p.is_file())
for p in files:
 assert p.suffix not in ['.dll','.pdb','.exe','.pyc'] and not p.name.endswith(('.tar.gz','.zip'));p.read_bytes().decode('utf-8')
index=folder/'completed-integrity.json';index.write_text(json.dumps({'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'verifier_sha256':sha(Path(__file__)),'scope':'All14fixedHTTPcases/10alternatingpairs/280freshprocesses/32warm12actual;exact224publishedproductionbytes,source/helper/binary/loaded-library/order/runtime/GC/actualaffinity/outputhashesverified. Quiet60 enforced by frozen runner; individual preflight observations were not retained. No launch excluded or pooled; original strict rules unchanged.','files':[{'path':str(p.relative_to(folder)),'sha256':sha(p)} for p in sorted(files)]},indent=2)+'\n')
archive=folder/'completed-text-only.tar.gz'
with tarfile.open(archive,'w:gz') as t:
 for p in files+[index]:t.add(p,arcname=str(p.relative_to(folder)),recursive=False)
print(json.dumps({'archive':str(archive),'sha256':sha(archive),'files':len(files)+1,'launches':280,'cases':14,'production_sources':224}))
