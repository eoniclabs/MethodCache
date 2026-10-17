from pathlib import Path
import datetime,hashlib,json,math,shutil,subprocess,tarfile
repo=Path('/Users/johan/.codex/worktrees/design-combined-gates-v1/MethodCache');source=Path('/tmp/methodcache-http-precision2-nimbus-v1');out=Path('/tmp/methodcache-http-precision2-nimbus-evidence-v1');sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest();arc=source/'complete-text-only.tar.gz';assert sha(arc)=='12c3b7561a9ead3aa51328b021e65c4e1ef25c460a8d51384ce642be44dc8a41';out.mkdir();raw=out/'raw';raw.mkdir()
with tarfile.open(arc,'r:gz')as tar:tar.extractall(raw,filter='data')
r=json.loads((raw/'completion-verification.json').read_text());assert r['launches']==40 and r['binary_files']==220 and r['candidate_source_files']==224 and r['own_labeled_containers_remaining']==0
for row in r['files']:assert sha(raw/row['path'])==row['sha256'],row['path']
assert sha(raw/'verify-complete.py')==sha(source/'verify-complete.py');i=json.loads((raw/'inputs.json').read_text());d=json.loads((raw/'prepared.json').read_text());intent=json.loads((raw/'dispatch-intent.json').read_text());sv=json.loads((raw/'source-verification.json').read_text())
assert sha(raw/'inputs.json')==d['inputs_sha256']==intent['inputs_sha256']==sv['inputs_sha256'];assert sha(raw/'prepared.json')==intent['prepared_sha256']==sv['prepared_sha256'];assert sha(raw/'source-verification.json')==intent['source_verification_sha256']
assert sha(raw/'origin-prepared.json')==i['origin_prepared_sha256'];assert i['pairs']==10 and i['warmups']==32 and len(i['cases'])==2
for p,h in i['files_sha256'].items():assert sha(raw/p)==h,p
for side,rev,count in [('base',i['base'],200),('head',i['published_runtime_equivalent'],224)]:
 rows=d['production_sources'][side];assert len(rows)==count
 result=subprocess.run(['git','cat-file','--batch'],cwd=repo,input=''.join(rev+':'+row['path']+'\n'for row in rows).encode(),capture_output=True,check=True);data=result.stdout;offset=0
 for row in rows:
  end=data.index(b'\n',offset);header=data[offset:end].split();assert len(header)==3 and header[1]==b'blob';length=int(header[2]);start=end+1;actual=hashlib.sha256(data[start:start+length]).hexdigest();offset=start+length+1
  if side=='head'and row['path']=='src/MethodCache.HttpCaching/README.md':
   doc=d['documentation_only_differences'][0];assert row['sha256']==doc['compiled_source_sha256']and actual==doc['published_documentation_sha256']
  else:assert row['sha256']==actual,row['path']
 assert offset==len(data)
assert d['source_hashes']['head']==d['source_hashes']['base']and len(d['source_hashes']['head'])==8
for x in d['historical_harness_differences']:assert sha(raw/'origin-harness'/Path(x['path']).name)==x['measured_sha256']
g=raw/'results/group-00';assert(raw/'exit').read_text().strip()=='0'and(g/'exit').read_text().strip()=='0';launches=[json.loads(l)for l in(g/'launches.jsonl').read_text().splitlines()];expected=[(side,n,c)for n in range(1,11)for c in i['cases']for side in(['base','head']if n%2 else['head','base'])];assert len(launches)==40 and[(x['side'],x['pair'],x['case'])for x in launches]==expected
observations=json.loads((g/'observations.json').read_text());assert observations and all(not o['external']for o in observations)
records={}
for row in launches:
 p=g/row['output'];assert sha(p)==row['sha256'];lines=p.read_text().splitlines();assert len(lines)==1;x=json.loads(lines[0]);assert x['case']==row['case'];assert x['warmupCount']==len(x['warmupMeasurements'])==32 and x['actualIterations']==len(x['actualMeasurements'])==12
 assert x['runtime']=='.NET 10.0.12'and not x['serverGC']and x['gcLatencyMode']=='Interactive'and x['affinityMask']==65535
 assert all(m['Operations']==8192 and math.isfinite(m['Nanoseconds'])and m['Nanoseconds']>0 for m in x['actualMeasurements']);assert math.isfinite(x['ns'])and x['ns']>0
 assert any(row['started_utc']<=o['utc']<=row['finished_utc']for o in observations)
 for o in observations:
  for cpus in o['thread_affinities'].values():assert cpus and set(cpus)<=set(range(16))
 records[row['side'],row['pair'],row['case']]=x
assert len(records)==40
paired=out/'paired-input';paired.mkdir()
for side in ['base','head']:
 for n in range(1,11):(paired/f'{side}-{n}.jsonl').write_text('\n'.join(json.dumps(records[side,n,c])for c in i['cases'])+'\n')
exits={}
for label,rules in [('original',raw/'accepted.json'),('accepted',repo/'benchmarks/MethodCache.CostProfile/accepted.json')]:
 assert sha(rules)=='5f1358997312fcd369488172a4a224b0582607b8bd74f492c411a2c93dc141b6';shutil.copyfile(rules,out/(label+'-rules.json'))
 result=subprocess.run(['python3',str(raw/'paired.py'),str(paired),'--accepted',str(rules),'--markdown',str(out/(label+'-timing.md'))],capture_output=True,text=True);(out/(label+'-review.log')).write_text(result.stdout+result.stderr);(out/(label+'-review.exit')).write_text(str(result.returncode)+'\n');exits[label]=result.returncode;print(label,result.stdout,'reviewExit',result.returncode)
shutil.copyfile(Path(__file__),out/'review-local.py');(out/'validation.json').write_text(json.dumps({'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'scope':'Separatecomplete2caseprecisionconfirmation;40freshprocesses/10pairs/32warm12actual8192op;allrawhashes/order/runtimeGC/affinity/sourceprovenanceverified;200baseline/224published6aGitblobsverifiedwithoneuncompiledREADMEexception.8samehistoricalharnesssourcesbothsides;2explicitMac-onlyQoSguarddifferencesdonotaffectLinuxselectedColdmethods.220binaries/loadedlibrariesverifiedremotebyexactpreservedverifier. No pooling/omissions/newbounds;originalfullMacfailureand3slowcasesremainseparate.','archive_sha256':sha(arc),'archive_bytes':arc.stat().st_size,'cases':i['cases'],'pairs':10,'launches':40,'review_exits':exits,'files':[{'path':str(p.relative_to(out)),'sha256':sha(p)}for p in sorted(raw.rglob('*'))if p.is_file()]},indent=2)+'\n')
