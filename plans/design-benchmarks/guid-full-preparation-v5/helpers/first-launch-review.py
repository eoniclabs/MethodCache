from pathlib import Path
import json,hashlib,re,datetime
f=Path.home()/'methodcache-design-bench-20261015/guid-final-v5';o=f/'timing-results';m=json.loads((o/'metadata.json').read_text());row=next(r for r in m['runs'] if (r['round'],r['revision'],r['group'])==(1,'final','local-contract'));assert row['exit']==0
stem='round-1-final-local-contract';aff=json.loads((o/(stem+'-affinity.json')).read_text());guard=json.loads((o/(stem+'-interference.json')).read_text());assert guard==[]
cpus=list(range(16));assert aff['required_cpus']==cpus and len(set(aff['benchmark_pids']))==27 and {str(aff['root_pid']),*aff['benchmark_pids']}.issubset(aff['processes']);assert all(x['allowed_cpus']==cpus and not x['mismatch'] and x['observations']>0 for x in aff['processes'].values())
reports=[];names=[]
for p in sorted((o/stem/'results').glob('*-report-full*.json')):
 d=json.loads(p.read_text());env=d['HostEnvironmentInfo'];assert env['DotNetCliVersion']=='10.0.401' and env['RuntimeVersion'].startswith('.NET 10.0.12')
 for case in d['Benchmarks']:
  actual=[x for x in case['Measurements'] if (x['IterationMode'],x['IterationStage'])==('Workload','Actual')];assert len(actual)==12
  names.append(case['FullName'])
 reports.append({'path':str(p),'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'cases':len(d['Benchmarks'])})
assert len(names)==len(set(names))==27
receipt={'collected_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'protocol_sha256':m['protocol_sha256'],'prepared_manifest_sha256':m['prepared_manifest_sha256'],'metadata_sha256_at_collection':hashlib.sha256((o/'metadata.json').read_bytes()).hexdigest(),'successful_launch':row,'verified_cases':27,'actual_iterations_per_case':12,'all_child_affinities_verified':True,'interference':guard,'affinity':aff,'reports':reports,'case_names':sorted(names),'means_assessed':False,'full_series_acceptance':False}
print(json.dumps(receipt,indent=2))
