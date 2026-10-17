"""Independently verify complete current HTTP evidence and retain the strict timing verdict."""
from pathlib import Path
import hashlib,json,math,shutil,subprocess,tarfile
repo=Path('/Users/johan/.codex/worktrees/design-combined-gates-v1/MethodCache');archive=Path('/tmp/methodcache-http-final14-compact-complete.tar.gz');expected_archive='677e4b14f35fb82e3fe0117de7c37f2c99d7aabe9619ccb403780f71a57f271f';sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
assert sha(archive)==expected_archive
out=Path('/tmp/methodcache-http-final14-compact-evidence-v1');out.mkdir();raw=out/'raw';raw.mkdir()
with tarfile.open(archive)as t:t.extractall(raw,filter='data')
index=json.loads((raw/'completed-integrity.json').read_text());remote_verifier=Path('/tmp/methodcache-verify-http-final14-compact-remote-v1.py');assert sha(remote_verifier)==index['verifier_sha256']=='825dc4fe25de45a71bc403b5125f94aa0ce256732c7657dd64a4b9ffc7fb5c8c'
for r in index['files']:
 p=raw/r['path'];assert p.resolve().is_relative_to(raw.resolve())and sha(p)==r['sha256'],r['path']
i=json.loads((raw/'inputs.json').read_text());d=json.loads((raw/'prepared.json').read_text());assert sha(raw/'inputs.json')==d['inputs_sha256']=='1a6616becd5f82bf5fc1868b330f34fbb0fa1d05bbc1a412280bfc44664fad36'
assert len(i['cases'])==len(set(i['cases']))==14 and i['pairs']==10 and i['warmups']==32
assert d['published_source_checkpoint']==i['published_source_checkpoint']=='5257fb63fb72393c817d74292e989299ede374d2'
assert d['source_hashes']['base']==d['source_hashes']['head']
for n,h in i['files_sha256'].items():assert sha(raw/n)==h,n
source_index=json.loads((raw/'frozen-harness-source-index.json').read_text());assert source_index['prepared_sha256']==sha(raw/'prepared.json')and len(source_index['files'])==10
for r in source_index['files']:
 assert sha(raw/'frozen-harness-source'/r['path'])==r['sha256']
 if r['path']in d['source_hashes']['head']:assert d['source_hashes']['head'][r['path']]==r['sha256']
# Compare both complete production inventories with actual Git blobs, not another manifest.
for side,rev,count in [('base','f43854dbcd3646893f490e78a0a1d9651e9c4521',200),('head','5257fb63fb72393c817d74292e989299ede374d2',224)]:
 rows=d['production_sources'][side];assert len(rows)==count
 result=subprocess.run(['git','cat-file','--batch'],cwd=repo,input=''.join(rev+':'+r['path']+'\n'for r in rows).encode(),capture_output=True,check=True);data=result.stdout;offset=0
 for r in rows:
  end=data.index(b'\n',offset);header=data[offset:end].split();assert len(header)==3 and header[1]==b'blob';length=int(header[2]);start=end+1;body=data[start:start+length];assert hashlib.sha256(body).hexdigest()==r['sha256'],r['path'];offset=start+length+1
 assert offset==len(data)
records={};ordered_cases=[];library_count=0
remote_root=Path('/home/agent-admin/methodcache-design-bench-20261015/http-final14-compact-v1')
for number,group in enumerate(d['groups']):
 g=raw/'results'/f'group-{number:02d}';assert(g/'exit').read_text().strip()=='0';ordered_cases.extend(x['case']for x in group)
 launches=[json.loads(l)for l in(g/'launches.jsonl').read_text().splitlines()]
 order=[(s,n,x['case'])for n in range(1,11)for x in group for s in(['base','head']if n%2 else['head','base'])]
 assert[(r['side'],r['pair'],r['case'])for r in launches]==order and len(launches)==40
 observations=json.loads((g/'observations.json').read_text());assert observations and all(not r['external']for r in observations)
 assert all(cpus and set(cpus).issubset(range(16))for r in observations for cpus in r['thread_affinities'].values())
 for r,key in zip(launches,order):
  p=g/r['output'];assert p.resolve().is_relative_to(g.resolve())and sha(p)==r['sha256']and key not in records
  x=json.loads(p.read_text());assert x['case']==r['case']and x['warmupCount']==len(x['warmupMeasurements'])==32 and x['actualIterations']==len(x['actualMeasurements'])==12
  assert x['runtime']=='.NET 10.0.12'and not x['serverGC']and x['gcLatencyMode']=='Interactive'
  assert isinstance(x['ns'],(int,float))and not isinstance(x['ns'],bool)and math.isfinite(x['ns'])and x['ns']>0
  assert all(m['Operations']>0 and math.isfinite(m['Nanoseconds'])and m['Nanoseconds']>0 for m in x['actualMeasurements'])
  growing=x['case'].startswith(('HttpResponsePathBenchmarks.Miss(','DesignHttpWorkloadBenchmarks.Miss('));assert x['growingSetFixedInvocations']==(8192 if growing else None)
  if growing:assert all(m['Operations']==8192 for m in x['actualMeasurements'])
  assert any(o['thread_affinities']and r['started_utc']<=o['utc']<=r['finished_utc']for o in observations)
  for lib in x['libraries']:
   p=Path(lib['location']);assert p.is_relative_to(remote_root/'bin'/r['side']/'main');assert d['binaries'][r['side']+'/main'][str(p.relative_to(remote_root))]==lib['sha256'].lower();library_count+=1
  records[key]=x
assert len(records)==280 and ordered_cases==i['cases']and len(d['groups'])==7
paired=out/'paired-input';paired.mkdir()
for side in['base','head']:
 for n in range(1,11):(paired/f'{side}-{n}.jsonl').write_text('\n'.join(json.dumps(records[side,n,c])for c in i['cases'])+'\n')
rules=repo/'benchmarks/MethodCache.CostProfile/accepted.json';assert sha(rules)=='5f1358997312fcd369488172a4a224b0582607b8bd74f492c411a2c93dc141b6';(out/'accepted-rules.json').write_bytes(rules.read_bytes())
result=subprocess.run(['python3',str(repo/'benchmarks/MethodCache.CostProfile/paired.py'),str(paired),'--accepted',str(out/'accepted-rules.json'),'--markdown',str(out/'accepted-timing.md')],capture_output=True,text=True);(out/'review.log').write_text(result.stdout+result.stderr);(out/'review.exit').write_text(str(result.returncode)+'\n');print(result.stdout,'ReviewExit',result.returncode)
shutil.copyfile(remote_verifier,out/'verify-remote.py');shutil.copyfile(Path(__file__),out/'review-local.py')
(out/'validation.json').write_text(json.dumps({'scope':'Complete14casecurrentHTTPcohort,10pairs/280freshprocesses/32warm12actual. ActualGitblobsverifyall200baselineand224headproductionfiles;allarchive/source/helper/order/output/runtimeGC/affinityandloadedlibrarycataloguesverifyindependently. No samples excluded or pooled. Canonical time rules unchanged; inactive proposal not applied. Strict verdict is review.exit.','archive_sha256':expected_archive,'cases':14,'pairs':10,'launches':280,'loaded_library_references':library_count,'production_files':{'base':200,'head':224},'review_exit':result.returncode,'files':[{'path':str(p.relative_to(out)),'sha256':sha(p)}for p in sorted(raw.rglob('*'))if p.is_file()]},indent=2)+'\n')
