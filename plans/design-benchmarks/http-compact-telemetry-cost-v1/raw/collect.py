from pathlib import Path
import datetime,hashlib,json,os,subprocess,tarfile
root=Path.home()/'methodcache-design-bench-20261015';p=root/'http-compact-telemetry-cost-v1';sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
assert (root/'OWNER').read_text().strip()=='methodcache-design-20261015-01a13a93'
try:os.kill(int((p/'dispatcher.pid').read_text()),0)
except ProcessLookupError:pass
else:raise RuntimeError('Cost dispatcher still live; do not collect incomplete evidence')
assert (p/'exit').read_text().strip()=='0' and (p/'results/review.exit').read_text().strip()=='0'
i=json.loads((p/'inputs.json').read_text());receipt=json.loads((p/'receipt.json').read_text());assert receipt['inputs_sha256']==sha(p/'inputs.json') and receipt['review_exit']==0
assert sha(p/'run.py')==i['controller_sha256'] and sha(p/'accepted.json')==i['accepted_sha256'] and sha(p/'compare.py')==i['compare_sha256']
assert sha(p/'accepted.json')=='5f1358997312fcd369488172a4a224b0582607b8bd74f492c411a2c93dc141b6'
count=0;cases=[]
for side,data in i['sources'].items():
 assert sha(root/data['prepared'])==data['prepared_sha256']
 for n,h in data['binaries'].items():assert sha(root/n)==h,n;count+=1
 rows=[json.loads(l) for l in (p/'results'/f'{side}.jsonl').read_text().splitlines()];assert len(rows)==len({r['case'] for r in rows})==67 and all('error' not in r for r in rows);cases.append({r['case'] for r in rows})
assert cases[0]==cases[1] and count==receipt['binary_hashes_before_and_after']==sum(len(data['binaries']) for data in i['sources'].values())
label='methodcache-design-20261015-01a13a93-http-compact-telemetry-cost-v1'
assert not subprocess.check_output(['docker','ps','--filter','label=methodcache-design-bench='+label,'--format','{{.ID}}'],text=True).strip(),'Own exact-label containers still live'
files=[f for f in p.iterdir() if f.is_file() and f.name not in ['completed-integrity.json','completed-text-only.tar.gz']]+[f for f in (p/'results').rglob('*') if f.is_file()]
for f in files:f.read_bytes().decode('utf-8')
index=p/'completed-integrity.json';index.write_text(json.dumps({'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'scope':'All67f438vsfullcompacttelemetrycandidatecostcases;0executionerrors/remoteacceptedreview0/sameacceptedrules,allcataloguedbinaryfilesandcontroller/preparationhashesindependentlyreverified,ownexactlabelcontainersgone;nootherserviceschanged.','files':[{'path':str(f.relative_to(p)),'sha256':sha(f)} for f in sorted(files)]},indent=2)+'\n')
a=p/'completed-text-only.tar.gz'
with tarfile.open(a,'w:gz') as t:
 for f in files+[index]:t.add(f,arcname=str(f.relative_to(p)),recursive=False)
print(json.dumps({'archive':str(a),'sha256':sha(a),'files':len(files)+1,'cost_cases':67,'binary_files':count}))
