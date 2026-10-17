from pathlib import Path
import datetime,hashlib,json,os,subprocess,sys,time,shutil
folder=Path(__file__).resolve().parent;root=folder.parent;sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest();utc=lambda:datetime.datetime.now(datetime.timezone.utc).isoformat();i=json.loads((folder/'inputs.json').read_text())
assert (root/'OWNER').read_text().strip()=='methodcache-design-20261015-01a13a93'
assert sha(root/'leased-batch.py')=='4a83e0b505eaef6d91599479c8fa6c78ddc2958e698882740cca7ecad57a6830'
for n,h in i['files_sha256'].items():assert sha(folder/n)==h,n
if sys.argv[1]=='worker':
 old=root/'http-compact-telemetry-pilot-v1';d=json.loads((old/'prepared.json').read_text());bins={}
 for side in ['base','head']:
  for n,h in d['binaries'][side+'/main'].items():assert sha(old/n)==h,n
  shutil.copytree(old/'bin'/side/'main',folder/'bin'/side/'main')
  bins[side+'/main']={str(p.relative_to(folder)):sha(p) for p in sorted((folder/'bin'/side/'main').iterdir()) if p.is_file()}
  for n,h in d['binaries'][side+'/main'].items():assert sha(folder/'bin'/side/'main'/Path(n).name)==h,n
 for row in d['production_sources']['head']:assert sha(old/'tree'/row['path'])==row['sha256'],row['path']
 (folder/'tree').symlink_to(old/'tree',target_is_directory=True)
 groups=[[{'family':'main','case':c} for c in i['cases'][n:n+2]] for n in range(0,len(i['cases']),2)]
 (folder/'prepared.json').write_text(json.dumps({'inputs_sha256':sha(folder/'inputs.json'),'prepared_utc':utc(),'revisions':d['revisions'],'published_source_checkpoint':i['published_source_checkpoint'],'origin_prepared_sha256':sha(old/'prepared.json'),'source_hashes':d['source_hashes'],'production_sources':d['production_sources'],'binaries':bins,'groups':groups,'sdk':d['sdk'],'cpu':d['cpu'],'protocol':i['purpose'],'helper_original_sha256':d['helper_original_sha256']},indent=2)+'\n')
 print('All14case frozen binaries copied and verified; no measurement started',flush=True)
else:
 assert not (folder/'prepare-started.json').exists();(folder/'prepare-started.json').write_text(json.dumps({'utc':utc(),'inputs_sha256':sha(folder/'inputs.json')})+'\n')
 while True:
  with (folder/'prepare-worker.log').open('a') as log:code=subprocess.run(['flock','--exclusive','--nonblock','--conflict-exit-code','75','/srv/skribera-ci/ci-build-capacity.lock','python3',str(root/'leased-batch.py'),i['prepare_label'],'python3','-B','-u',str(Path(__file__)),'worker'],stdout=log,stderr=subprocess.STDOUT).returncode
  if code!=75:break
  time.sleep(5)
 (folder/'prepare.exit').write_text(str(code)+'\n');print('Preparation terminal',code,utc(),flush=True)
