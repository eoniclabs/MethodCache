"""Verify and review the complete fixed14case study; never select or pool launches."""
from pathlib import Path
import hashlib,json,subprocess,sys,tarfile,shutil
archive=Path(sys.argv[1]);expected_sha=sys.argv[2];sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest();assert sha(archive)==expected_sha
out=Path('/tmp/methodcache-http-cumulative-time32-evidence-v1');out.mkdir();raw=out/'raw';raw.mkdir()
with tarfile.open(archive) as t:t.extractall(raw,filter='data')
index=json.loads((raw/'completed-integrity.json').read_text())
for r in index['files']:assert sha(raw/r['path'])==r['sha256'],r['path']
remote_verifier=Path('/tmp/methodcache-verify-http-cumulative32-remote-v1.py');assert sha(remote_verifier)==index['verifier_sha256'];shutil.copyfile(remote_verifier,out/'verify-remote.py')
shutil.copyfile(Path(__file__),out/'review-local.py')
assert sha(raw/'confirmation-scope.json')=='a5c19f63465b80bf588cddf1605383d5f5bd17331a9f360c440b944c84606a61'
i=json.loads((raw/'inputs.json').read_text());d=json.loads((raw/'prepared.json').read_text());assert len(i['cases'])==len(set(i['cases']))==14 and d['inputs_sha256']==sha(raw/'inputs.json')
assert d['source_hashes']['base']==d['source_hashes']['head']
source_index=json.loads((raw/'frozen-harness-source-index.json').read_text());assert source_index['prepared_sha256']==sha(raw/'prepared.json') and len(source_index['files'])==13
for r in source_index['files']:
 assert sha(raw/'frozen-harness-source'/r['path'])==r['sha256']
 if r['prepared_source_catalogue']:assert r['sha256']==d['source_hashes']['head'][r['path']]
records={}
for number,group in enumerate(d['groups']):
 g=raw/'results'/f'group-{number:02d}';assert (g/'exit').read_text().strip()=='0'
 launches=[json.loads(l) for l in (g/'launches.jsonl').read_text().splitlines()]
 expected=[(s,n,x['case']) for n in range(1,11) for x in group for s in (['base','head'] if n%2 else ['head','base'])]
 assert [(r['side'],r['pair'],r['case']) for r in launches]==expected and len(launches)==40
 for r,key in zip(launches,expected):
  p=g/r['output'];assert sha(p)==r['sha256'];assert key not in records;records[key]=json.loads(p.read_text())
assert len(records)==280
paired=out/'paired-input';paired.mkdir()
for side in ['base','head']:
 for pair in range(1,11):(paired/f'{side}-{pair}.jsonl').write_text('\n'.join(json.dumps(records[side,pair,c]) for c in i['cases'])+'\n')
repo=Path('/Users/johan/.codex/worktrees/design-combined-gates-v1/MethodCache');rules=repo/'benchmarks/MethodCache.CostProfile/accepted.json'
frozen=subprocess.check_output(['git','show','2a9b47dc3d8a7373dd4a5e01a4e8007b7c307632:benchmarks/MethodCache.CostProfile/accepted.json'],cwd=repo)
assert hashlib.sha256(frozen).hexdigest()==sha(rules)=='5f1358997312fcd369488172a4a224b0582607b8bd74f492c411a2c93dc141b6'
(out/'original-rules.json').write_bytes(frozen);(out/'accepted-rules.json').write_bytes(rules.read_bytes())
verdicts={}
for label in ['original','accepted']:
 result=subprocess.run(['python3',str(repo/'benchmarks/MethodCache.CostProfile/paired.py'),str(paired),'--accepted',str(out/(label+'-rules.json')),'--markdown',str(out/(label+'-timing.md'))],text=True,capture_output=True)
 (out/(label+'-review.log')).write_text(result.stdout+result.stderr);(out/(label+'-review.exit')).write_text(str(result.returncode)+'\n');verdicts[label]=result.returncode
 print(label,result.stdout,'review exit',result.returncode)
(out/'validation.json').write_text(json.dumps({'scope':'Complete14predeclaredHTTPcases,10alternatingpairs,280launches;allrawarchive/sourcebody/protocolhashesverified. No launch excluded or pooled. Fivegrowing-setmissesretainpressurewithsame8192invocationsperiterationonbothsides. No new accepted rule. Timing verdict is each reviewer exit.','archive_sha256':expected_sha,'launches':280,'cases':14,'pairs':10,'warmups':32,'actual_iterations':12,'review_exits':verdicts,'files':[{'path':str(p.relative_to(out)),'sha256':sha(p)} for p in sorted(raw.rglob('*')) if p.is_file()]},indent=2)+'\n')
print('Whole14case evidence complete; preserve the actual verdict, including failures or insufficient precision')
