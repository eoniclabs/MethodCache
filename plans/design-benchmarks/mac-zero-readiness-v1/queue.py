from pathlib import Path
import subprocess,json,time,signal,datetime
f=Path(__file__).resolve().parent;child=None
signal.signal(signal.SIGTERM,lambda a,b:(_ for _ in ()).throw(SystemExit(128+a)))
try:
 tasks=[('prepare',None)]+[('measure',row['pr']) for row in json.loads((f/'inputs.json').read_text())['comparisons']]
 for mode,index in tasks:
  name=mode if index is None else mode+'-'+index
  while True:
   with (f/(name+'-driver.log')).open('a') as log:child=subprocess.Popen(['python3','-B','-u',str(f/'run.py'),mode]+([] if index is None else [index]),stdin=subprocess.DEVNULL,stdout=log,stderr=subprocess.STDOUT,start_new_session=True);(f/(name+'.pid')).write_text(str(child.pid)+'\n');code=child.wait();child=None
   if code!=75:break
   time.sleep(5)
  (f/(name+'.exit')).write_text(str(code)+'\n')
  if code!=0:(f/'sequence.exit').write_text(str(code)+'\n');raise RuntimeError((name,code))
  print('Completefixedstage',name,datetime.datetime.now(datetime.timezone.utc).isoformat(),flush=True);time.sleep(10)
 (f/'sequence.exit').write_text('0\n')
finally:
 if child is not None and child.poll() is None:
  child.terminate()
  try:child.wait(timeout=30)
  except subprocess.TimeoutExpired:child.kill();child.wait()
