from pathlib import Path
import hashlib,json,math,os,tarfile,datetime
folder=Path.home()/'methodcache-design-bench-20261015/http-cumulative-time32-v1'
root=folder.parent;sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
assert (root/'OWNER').read_text().strip()=='methodcache-design-20261015-01a13a93'
try:os.kill(int((folder/'dispatcher.pid').read_text()),0)
except ProcessLookupError:pass
else:raise RuntimeError('Entire fixed cohort still live; do not inspect partial means')
inputs=json.loads((folder/'inputs.json').read_text());prepared=json.loads((folder/'prepared.json').read_text());start=json.loads((folder/'timing-start.json').read_text());preflight=json.loads((folder/'independent-preflight.json').read_text())
assert sha(folder/'inputs.json')==prepared['inputs_sha256']==start['inputs_sha256']==preflight['inputs_sha256']
assert sha(folder/'prepared.json')==start['prepared_sha256']==preflight['prepared_sha256']
for n,h in inputs['files_sha256'].items():assert sha(folder/n)==h,n
for rows in prepared['binaries'].values():
 for n,h in rows.items():assert sha(folder/n)==h,n
prior=root/'http-deferred-id-experiment-v1'
for side in ['base','head']:
 for row in prepared['production_sources'][side]:assert sha((root/'final-paired-cost-v2/base' if side=='base' else prior/'tree')/row['path'])==row['sha256'],row['path']
 for n,h in prepared['source_hashes'][side].items():assert sha((root/'final-paired-cost-v2/base' if side=='base' else prior/'tree')/n)==h,n
source_index=json.loads((folder/'frozen-harness-source-index.json').read_text())
assert source_index['prepared_sha256']==sha(folder/'prepared.json')
for row in source_index['files']:assert sha(folder/'frozen-harness-source'/row['path'])==row['sha256']
records={}
for number,group in enumerate(prepared['groups']):
 d=folder/'results'/f'group-{number:02d}';assert (d/'exit').read_text().strip()=='0'
 launches=[json.loads(x) for x in (d/'launches.jsonl').read_text().splitlines()]
 expected=[(side,pair,item['case']) for pair in range(1,11) for item in group for side in (['base','head'] if pair%2 else ['head','base'])]
 keys=[(r['side'],r['pair'],r['case']) for r in launches];assert keys==expected and len(keys)==len(set(keys))==40
 observations=json.loads((d/'observations.json').read_text());assert observations and all(not o['external'] for o in observations)
 assert all(cpus and set(cpus).issubset(set(range(16))) for o in observations for cpus in o['thread_affinities'].values())
 for r,key in zip(launches,keys):
  p=d/r['output'];assert p.resolve().is_relative_to(d.resolve()) and sha(p)==r['sha256']
  x=json.loads(p.read_text());assert x['case']==r['case']
  assert x['warmupCount']==len(x['warmupMeasurements'])==32 and x['actualIterations']==len(x['actualMeasurements'])==12
  growing=x['case'].startswith('HttpResponsePathBenchmarks.Miss(') or x['case'].startswith('DesignHttpWorkloadBenchmarks.Miss(')
  assert x['growingSetFixedInvocations']==(8192 if growing else None)
  if growing:assert all(m['Operations']==8192 for m in x['actualMeasurements'])
  assert x['runtime']=='.NET 10.0.12' and not x['serverGC'] and x['gcLatencyMode']=='Interactive'
  assert math.isfinite(x['ns']) and x['ns']>0 and all(m['Operations']>0 and math.isfinite(m['Nanoseconds']) and m['Nanoseconds']>0 for m in x['actualMeasurements'])
  assert any(o['thread_affinities'] and r['started_utc']<=o['utc']<=r['finished_utc'] for o in observations)
  assert key not in records;records[key]=x
assert len(records)==280
files=[p for p in folder.iterdir() if p.is_file() and p.name not in ['completed-integrity.json','completed-text-only.tar.gz','frozen-harness-source-text-only.tar.gz']]
for sub in ['results','preparation','project','frozen-harness-source']:
 files += [p for p in (folder/sub).rglob('*') if p.is_file() and not any(x in ['bin','obj','__pycache__'] for x in p.relative_to(folder/sub).parts)]
assert all(p.suffix not in ['.dll','.pdb','.exe','.pyc'] and not p.name.endswith(('.tar.gz','.zip')) for p in files)
for p in files:p.read_bytes().decode('utf-8')
validation=folder/'completed-integrity.json';validation.write_text(json.dumps({'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'verifier_sha256':sha(Path(__file__)),'scope':'All14cases/10alternatingpairs/280freshprocesses;5growing-setcasesretainpressurewith8192invocations/iterationonbothsideswithoutreset,32warmups12actual;fullcohortterminal,allsource/binary/runtime/affinity/order/outputhashesverified. Quiet60 enforced by frozen runner; no individual preflight samples were stored. No paired means read before this check.','files':[{'path':str(p.relative_to(folder)),'sha256':sha(p)} for p in sorted(files)]},indent=2)+'\n')
archive=folder/'completed-text-only.tar.gz'
with tarfile.open(archive,'w:gz') as t:
 for p in files+[validation]:t.add(p,arcname=str(p.relative_to(folder)),recursive=False)
print(json.dumps({'archive':str(archive),'sha256':sha(archive),'files':len(files)+1,'rows':len(records)}))
