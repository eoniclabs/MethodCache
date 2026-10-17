from pathlib import Path
import subprocess,json,time,signal,datetime
f=Path(__file__).resolve().parent;child=None
signal.signal(signal.SIGTERM,lambda a,b:(_ for _ in ()).throw(SystemExit(128+a)))
try:
 inputs=json.loads((f/'inputs.json').read_text());ci=Path(inputs['wait_ci_directory']);observer=Path(inputs['wait_identity_observer'])
 assert (ci/'commit').read_text().strip()==inputs['current_source_equivalent_published_head']
 jobs={'linux':['build','demo','tests','diffcoverage','properties','soak','cultures','aot','packages','docs'],'macos':['build','unit'],'windows':['build','unit']}
 required=[ci/platform/(name+'.exit') for platform,names in jobs.items() for name in names]
 while True:
  bad=[str(p) for p in required if p.exists() and p.read_text().strip()!='0']
  if bad:(f/'sequence.exit').write_text('1\n');raise RuntimeError(('Exact prerequisite CI failed',bad))
  if all(p.exists() and p.read_text().strip()=='0' for p in required) and (observer/'exit').exists():break
  time.sleep(20)
 assert (observer/'exit').read_text().strip()=='0'
 print('Exact CI prerequisites and bounded observer complete',datetime.datetime.now(datetime.timezone.utc).isoformat(),flush=True)
 for row in json.loads((f/'inputs.json').read_text())['comparisons']:
  name='measure-'+row['pr']
  while True:
   with (f/(name+'-driver.log')).open('a') as log:child=subprocess.Popen(['python3','-B','-u',str(f/'run.py'),'measure',row['pr']],stdin=subprocess.DEVNULL,stdout=log,stderr=subprocess.STDOUT,start_new_session=True);(f/(name+'.pid')).write_text(str(child.pid)+'\n');code=child.wait();child=None
   if code!=75:break
   time.sleep(5)
  (f/(name+'.exit')).write_text(str(code)+'\n')
  if code!=0:(f/'sequence.exit').write_text(str(code)+'\n');raise RuntimeError((name,code))
  print('Completefixedgroup',row['pr'],datetime.datetime.now(datetime.timezone.utc).isoformat(),flush=True);time.sleep(10)
 (f/'sequence.exit').write_text('0\n')
finally:
 if child is not None and child.poll() is None:
  child.terminate()
  try:child.wait(timeout=30)
  except subprocess.TimeoutExpired:child.kill();child.wait()
