"""Terminal-only independent review of all ten predeclared compact-scope CPU cases."""
from pathlib import Path
import datetime,hashlib,json,math,os,shutil,subprocess
source=Path('/tmp/methodcache-client-index-final14-v1');out=Path('/tmp/methodcache-client-index-final14-evidence-v1');repo=Path('/Users/johan/.codex/worktrees/design-combined-gates-v1/MethodCache');sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
try:os.kill(int((source/'sequence.pid').read_text()),0)
except ProcessLookupError:pass
else:raise RuntimeError('Exact sequence still live; no partial cohort review')
assert (source/'sequence.exit').read_text().strip()=='0'
i=json.loads((source/'inputs.json').read_text());d=json.loads((source/'prepared.json').read_text());intent=json.loads((source/'sequence-intent.json').read_text())
assert sha(source/'inputs.json')==d['inputs_sha256']==intent['inputs_sha256']
assert sha(source/'prepared.json')==intent['prepared_sha256'] and sha(source/'queue.py')==intent['queue_sha256']
assert sha(source/'run.py')==i['controller_sha256'] and sha(source/'process_activity.py')==i['activity_sha256']
assert i['pairs']==10 and i['warmups']==32 and len(i['comparisons'])==7
cases=i['cases']['main']+i['cases']['int'];assert len(cases)==len(set(cases))==14
assert len(i['cases']['main'])==14 and not i['cases']['int']
for n,h in i['source_sha256'].items():
 assert sha(source/'harness'/n)==h
 for point in ['baseline','final']:assert sha(source/'trees'/point/n)==h
for n,h in i['frozen_reviewers'].items():assert sha(source/n)==h
for rows in d['binaries'].values():
 for r in rows:assert sha(source/r['path'])==r['sha256']
assert sum(map(len,d['binaries'].values()))==210
assert d['points']['baseline']=='f43854dbcd3646893f490e78a0a1d9651e9c4521'and d['points']['final']=='5257fb63fb72393c817d74292e989299ede374d2'
assert d['head_patch_sha256']==sha(source/'production.patch')=='92764360476331349eb6d56cc231727c16ac1721182c99c911e796d26f0fe109'
equivalence=json.loads((source/'published-runtime-equivalence.json').read_text());assert sha(source/'published-runtime-equivalence.json')==intent['published_runtime_equivalence_sha256']and equivalence['prepared_sha256']==sha(source/'prepared.json')
assert equivalence['published_head']=='c9ec0db33d1d5b27a68ff7a31d7437c603b360a5'and equivalence['source_files_audited']==224
assert len(equivalence['documentation_only_differences'])==1;doc=equivalence['documentation_only_differences'][0];assert doc['path']=='src/MethodCache.HttpCaching/README.md'
for point,rev,count in [('baseline','f43854dbcd3646893f490e78a0a1d9651e9c4521',200),('final',equivalence['published_head'],224)]:
 rows=d['production_sources'][point];assert len(rows)==count
 for r in rows:assert sha(source/'trees'/point/r['path'])==r['sha256']
 result=subprocess.run(['git','cat-file','--batch'],cwd=repo,input=''.join(rev+':'+r['path']+'\n'for r in rows).encode(),capture_output=True,check=True);data=result.stdout;offset=0
 for r in rows:
  end=data.index(b'\n',offset);h=data[offset:end].split();assert len(h)==3 and h[1]==b'blob';n=int(h[2]);start=end+1;actual=hashlib.sha256(data[start:start+n]).hexdigest();offset=start+n+1
  if point=='final'and r['path']==doc['path']:assert r['sha256']==doc['measured_sha256']and actual==doc['publication_sha256']
  else:assert actual==r['sha256'],r['path']
 assert offset==len(data)
records={}
for group in i['comparisons']:
 g=source/('pr-'+group['pr']);assert (g/'exit').read_text().strip()=='0' and (source/('measure-'+group['pr']+'.exit')).read_text().strip()=='0'
 request=json.loads((g/'intent.json').read_text());assert request['inputs_sha256']==sha(source/'inputs.json') and request['prepared_sha256']==sha(source/'prepared.json') and request['comparison']==group and request['cases']==group['cases']
 launches=[json.loads(l)for l in (g/'launches.jsonl').read_text().splitlines()]
 expected=[(side,pair,family,c)for pair in range(1,11)for family in ['main','int']for c in group['cases'][family]for side in (['base','head']if pair%2 else ['head','base'])]
 assert [(r['side'],r['pair'],r['family'],r['case'])for r in launches]==expected and len(launches)==len(set(expected))==40
 observed=json.loads((g/'observations.json').read_text());quiet=json.loads((g/'quiet-confirmed.json').read_text());readiness=[r for r in observed if r['phase']=='preflight']
 assert quiet['consecutive_quiet_samples']==60 and quiet['sample_period_seconds']==1 and quiet['reservation_deadline_seconds']==1470
 assert len(readiness)>=60 and all(not r['external'] and r['other_process_cpu_percent']<=quiet['other_process_cpu_limit'] for r in readiness[-60:])
 assert all(not r['external']for r in observed if r['phase']=='measurement')
 for row,key in zip(launches,expected):
  p=g/row['output'];assert p.resolve().is_relative_to(g.resolve())and sha(p)==row['sha256']
  x=json.loads(p.read_text());record_key=(row['side'],row['pair'],row['case']);assert record_key not in records
  assert x['case']==row['case'] and row['point']==group[row['side']]
  assert x['warmupCount']==len(x['warmupMeasurements'])==32 and x['actualIterations']==len(x['actualMeasurements'])==12
  assert x['runtime']=='.NET 10.0.3' and not x['serverGC'] and x['gcLatencyMode']=='Interactive'
  assert isinstance(x['ns'],(float,int)) and not isinstance(x['ns'],bool) and math.isfinite(x['ns']) and x['ns']>0
  assert all(m['Operations']>0 and math.isfinite(m['Nanoseconds'])and m['Nanoseconds']>0 for m in x['actualMeasurements'])
  growing=x['case'].startswith(('HttpResponsePathBenchmarks.Miss(','DesignHttpWorkloadBenchmarks.Miss('));assert x['growingSetFixedInvocations']==(8192 if growing else None)
  if growing:assert all(m['Operations']==8192 for m in x['actualMeasurements'])
  assert any(r['phase']=='measurement'and row['started_utc']<=r['utc']<=row['finished_utc']for r in observed)
  catalog={r['path']:r['sha256']for r in d['binaries'][row['point']+'/'+row['family']]}
  for lib in x['libraries']:
   path=Path(lib['location']);assert path.resolve().is_relative_to((source/'bin'/row['point']/row['family']).resolve())and sha(path)==lib['sha256'].lower()
   assert catalog[str(path.resolve().relative_to(source.resolve()))]==lib['sha256'].lower()
  records[record_key]=x
assert len(records)==280
out.mkdir();raw=out/'raw';raw.mkdir()
for p in source.iterdir():
 if p.is_file()and p.name!='source.tar':shutil.copyfile(p,raw/p.name)
for name in ['harness','helper']:
 shutil.copytree(source/name,raw/name,ignore=shutil.ignore_patterns('bin','obj','__pycache__'))
for group in i['comparisons']:shutil.copytree(source/('pr-'+group['pr']),raw/('pr-'+group['pr']))
shutil.copyfile(Path(__file__),out/'review-local.py')
paired=out/'paired-input';paired.mkdir()
for side in ['base','head']:
 for pair in range(1,11):(paired/f'{side}-{pair}.jsonl').write_text('\n'.join(json.dumps(records[side,pair,c])for c in cases)+'\n')
results={}
for label,rules in [('original',source/'accepted.json'),('accepted',repo/'benchmarks/MethodCache.CostProfile/accepted.json')]:
 if label=='original':assert sha(rules)=='5f1358997312fcd369488172a4a224b0582607b8bd74f492c411a2c93dc141b6'
 (out/(label+'-rules.json')).write_bytes(rules.read_bytes())
 r=subprocess.run(['python3',str(source/'paired.py'),str(paired),'--accepted',str(rules),'--markdown',str(out/(label+'-timing.md'))],capture_output=True,text=True)
 (out/(label+'-review.log')).write_text(r.stdout+r.stderr);(out/(label+'-review.exit')).write_text(str(r.returncode)+'\n');results[label]=r.returncode;print(label,r.stdout,'reviewExit',r.returncode)
(out/'validation.json').write_text(json.dumps({'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'scope':'Complete14fixedHTTPcasecandidateconfirmation/10pairs/280freshprocesses/32warm12actual;5growing-setcasesretainpressure8192invocationsperiterationwithoutreset. All200baseline/224candidatefilesandactualGitblobsverify;runtimecode/buildinputsbyteidenticaltopublishedc9ec0db3withoneexplicituncompiledREADMEdifference. All210binaryfiles/loadedlibraries/runtimeGC/order/outputhashes/quietadmissionverified. No omitted launches or pooling. Original and current rule verdicts retained separately; no automatic acceptance of inactive proposals.','inputs_sha256':sha(source/'inputs.json'),'prepared_sha256':sha(source/'prepared.json'),'cases':14,'pairs':10,'launches':280,'candidate_production_sources':224,'baseline_production_sources':200,'binary_files':210,'review_exits':results,'files':[{'path':str(p.relative_to(out)),'sha256':sha(p)}for p in sorted(raw.rglob('*'))if p.is_file()]},indent=2)+'\n')
