from pathlib import Path
import datetime,hashlib,json,os,subprocess,time,traceback
p=Path(__file__).resolve().parent;sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest();utc=lambda:datetime.datetime.now(datetime.timezone.utc).isoformat();deadline=time.monotonic()+7200
try:
 while not (p/'prepare.exit').exists():
  pid=int((p/'prepare-dispatcher.pid').read_text())
  try:os.kill(pid,0)
  except ProcessLookupError:raise RuntimeError('Preparation handle missing without terminal receipt; preserve state for inspection')
  assert time.monotonic()<deadline,'Observer timed out; do not restart preparation'
  time.sleep(5)
 assert (p/'prepare.exit').read_text().strip()=='0'
 i=json.loads((p/'inputs.json').read_text());d=json.loads((p/'prepared.json').read_text());assert sha(p/'inputs.json')==d['inputs_sha256']
 for n,h in i['files_sha256'].items():assert sha(p/n)==h,n
 for values in d['binaries'].values():
  for n,h in values.items():assert sha(p/n)==h,n
 source=json.loads((p/'published-source-equivalence.json').read_text());actual={r['path']:r['sha256'] for r in d['production_sources']['head']};expected={r['path']:r['sha256'] for r in source['sources']};assert actual==expected,'Prepared production inventory differs from actual published source snapshot'
 for n,h in expected.items():assert sha(p/'tree'/n)==h,n
 (p/'published-source-equivalence-verified.json').write_text(json.dumps({'utc':utc(),'published_head':source['published_head'],'source_files':len(expected),'source_catalogue_sha256':sha(p/'published-source-equivalence.json'),'prepared_sha256':sha(p/'prepared.json'),'scope':'Every tracked production source byte matches published5257fb63; recorded build flags/actual libraries retain their own provenance.'},indent=2)+'\n')
 assert not (p/'dispatcher.pid').exists()
 with (p/'dispatcher.log').open('x') as log:c=subprocess.Popen(['python3','-B','-u',str(p/'run.py'),'run'],stdin=subprocess.DEVNULL,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
 (p/'dispatcher.pid').write_text(str(c.pid)+'\n');print('Verified all published production sources and frozen binaries; final14 dispatcherPID',c.pid,utc(),flush=True)
 (p/'after-prepare.exit').write_text('0\n')
except BaseException:
 traceback.print_exc();(p/'after-prepare.exit').write_text('1\n');raise
