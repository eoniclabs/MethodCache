"""Run only the explicit remaining F05 coverage after current independent CPU confirmations."""
from pathlib import Path
import hashlib,json,os,signal,subprocess,time,datetime
folder=Path(__file__).resolve().parent
previous=Path('/tmp/methodcache-local-cpu-v5')
child=None
utc=lambda:datetime.datetime.now(datetime.timezone.utc).isoformat()
digest=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
def stop(signum,frame):raise SystemExit(128+signum)
signal.signal(signal.SIGTERM,stop)

def phase(mode,*arguments):
 global child
 name='-'.join([mode,*arguments]);logfile=folder/(name+'-driver.log')
 print('Waiting for bounded phase',name,utc(),flush=True)
 while True:
  with logfile.open('a') as log:
   child=subprocess.Popen(['python3','-B','-u',str(folder/'run.py'),mode,*arguments],stdin=subprocess.DEVNULL,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
   (folder/(name+'.pid')).write_text(str(child.pid)+'\n')
   code=child.wait();child=None
  if code!=75:break
  time.sleep(5) # Explicitbusyexit means no attempt began.
 (folder/(name+'.exit')).write_text(str(code)+'\n')
 assert code==0,(name,code)
 print('Finished bounded phase',name,utc(),flush=True)
 time.sleep(10)

try:
 print('Waiting for existing Mac sequence handle69994',utc(),flush=True)
 while True:
  try:os.kill(69994,0)
  except ProcessLookupError:break
  time.sleep(2)
 assert (previous/'sequence.exit').read_text().strip()=='0','Preserve current CPU study failure; no automatic restarts'
 phase('prepare')
 inputs=json.loads((folder/'inputs.json').read_text());prepared=json.loads((folder/'prepared.json').read_text())
 assert prepared['inputs_sha256']==digest(folder/'inputs.json')
 count=0
 for point,sha in inputs['points'].items():
  tree=folder/'trees'/point
  assert subprocess.check_output(['git','-C',str(tree),'rev-parse','HEAD'],text=True).strip()==sha
  assert not subprocess.check_output(['git','-C',str(tree),'diff','--name-only',sha,'--','src'],text=True).strip()
  for relative,h in inputs['source_sha256'].items():assert digest(tree/relative)==h
  for family in ['main','int']:
   assert set(prepared['inventories'][point][family])==set(inputs['cases'][family])
   for row in prepared['binaries'][point+'/'+family]:
    assert digest(folder/row['path'])==row['sha256'];count+=1
  assert len((folder/(point+'-setup-guard.log')).read_text().splitlines())==4
 receipt={'utc':utc(),'compiled_files_verified':count,'points':inputs['points'],'source_and_case_inventories_verified':True,'production_diff_empty':True,'prepared_sha256':digest(folder/'prepared.json'),'inputs_sha256':digest(folder/'inputs.json'),'scope':'All48prefixcostcases;28fullpathGUIDhit/missandintcontroltimecases;7boundedgroups,10pairs/case'}
 (folder/'independent-preflight.json').write_text(json.dumps(receipt,indent=2)+'\n')
 phase('cost')
 for row in inputs['comparisons']:phase('measure',row['pr'])
 (folder/'sequence.exit').write_text('0\n')
 print('All explicit F05 measurement work terminal; full result review remains',utc(),flush=True)
finally:
 if child is not None and child.poll() is None:
  child.terminate()
  try:child.wait(timeout=30)
  except subprocess.TimeoutExpired:child.kill();child.wait()
