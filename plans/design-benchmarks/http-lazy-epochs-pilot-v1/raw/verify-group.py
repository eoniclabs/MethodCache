"""Independently check a complete group's provenance/guard logs, without reading timing means."""
from pathlib import Path
import datetime,hashlib,json,subprocess,sys
mac=sys.argv[1]=='mac';number=int(sys.argv[2]);sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
f=Path('/tmp/methodcache-nonhttp-warm32-v2') if mac else Path.home()/'methodcache-design-bench-20261015/sparse-tags-lazy-epochs-pilot-v1'
i=json.loads((f/'inputs.json').read_text());d=json.loads((f/'prepared.json').read_text());assert d['inputs_sha256']==sha(f/'inputs.json')
if mac:
 comparison=i['comparisons'][number];name=comparison['pr'];g=f/('pr-'+name);cases=comparison['cases']['main']
 intent=json.loads((g/'intent.json').read_text());assert intent['inputs_sha256']==sha(f/'inputs.json') and intent['prepared_sha256']==sha(f/'prepared.json') and intent['comparison']==comparison
else:
 name=f'{number:02d}';g=f/'results'/('group-'+name);cases=[r['case'] for r in d['groups'][number]]
assert (g/'exit').read_text().strip()=='0'
launches=[json.loads(s) for s in (g/'launches.jsonl').read_text().splitlines()]
expected=[(s,n,c) for n in range(1,i['pairs']+1) for c in cases for s in (['base','head'] if n%2 else ['head','base'])]
assert [(r['side'],r['pair'],r['case']) for r in launches]==expected
obs=json.loads((g/'observations.json').read_text());assert obs
if mac:
 quiet=json.loads((g/'quiet-confirmed.json').read_text());ready=[o for o in obs if o['phase']=='preflight'];assert quiet['consecutive_quiet_samples']==60 and len(ready)>=60
 assert all(not o['external'] and o['other_process_cpu_percent']<=quiet['other_process_cpu_limit'] for o in ready[-60:]);measured=[o for o in obs if o['phase']=='measurement']
else:
 measured=obs;assert all(cpus and set(cpus).issubset(set(range(16))) for o in measured for cpus in o['thread_affinities'].values())
assert measured and all(not o['external'] for o in measured)
for r in launches:
 path=g/r['output'];assert sha(path)==r['sha256'];x=json.loads(path.read_text());assert x['case']==r['case'] and x['warmupCount']==len(x['warmupMeasurements'])==32 and x['actualIterations']==len(x['actualMeasurements'])==12
 assert x['runtime']==('.NET 10.0.3' if mac else '.NET 10.0.12') and not x['serverGC'] and x['gcLatencyMode']=='Interactive'
 assert any(r['started_utc']<=o['utc']<=r['finished_utc'] and (mac or o['thread_affinities']) for o in measured)
 if not mac:
  growing=x['case'].startswith('HttpResponsePathBenchmarks.Miss(') or x['case'].startswith('DesignHttpWorkloadBenchmarks.Miss(')
  assert x['growingSetFixedInvocations']==(8192 if growing else None)
  if growing:assert all(m['Operations']==8192 for m in x['actualMeasurements'])
result={'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'scope':'Complete fixed group integrity only; no partial timing means or acceptance decision. Entire fixed cohort still required.','group':name,'launches':len(launches),'observation_samples':len(measured),'external_builds':0,'inputs_sha256':sha(f/'inputs.json'),'prepared_sha256':sha(f/'prepared.json'),'verifier_sha256':sha(Path(__file__)),'files':[{'path':str(p.relative_to(f)),'sha256':sha(p)} for p in [g/'launches.jsonl',g/'observations.json',g/'exit']]}
(f/('completed-group-'+name+'-protocol-integrity.json')).write_text(json.dumps(result,indent=2)+'\n')
print('Complete group verified',('Mac' if mac else 'Nimbus'),name,'launches',len(launches),'zero external builds; full timing verdict remains pending')
