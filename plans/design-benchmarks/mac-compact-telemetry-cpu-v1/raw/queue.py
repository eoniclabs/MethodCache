from pathlib import Path
import datetime,importlib.util,json,os,signal,subprocess,sys,time
f=Path(__file__).resolve().parent;child=None;deadline=time.monotonic()+7200
utc=lambda:datetime.datetime.now(datetime.timezone.utc).isoformat()
spec=importlib.util.spec_from_file_location('runner',f/'run.py');runner=importlib.util.module_from_spec(spec);sys.modules[spec.name]=runner;spec.loader.exec_module(runner)
a=importlib.util.spec_from_file_location('activity',f/'process_activity.py');activity=importlib.util.module_from_spec(a);sys.modules[a.name]=activity;a.loader.exec_module(activity);runner.monitor_module=activity
signal.signal(signal.SIGTERM,lambda a,b:(_ for _ in ()).throw(SystemExit(128+a)))
try:
 for group in json.loads((f/'inputs.json').read_text())['comparisons']:
  index=group['pr'];name='measure-'+index
  while True:
   consecutive=0;monitor=activity.ActivityMonitor()
   with (f/'queue-readiness.jsonl').open('a') as log:
    while consecutive<60:
     assert time.monotonic()<deadline,'Bounded7200secondreadinesswindowexpired;preserveattempt'
     sample=runner.snapshot(monitor);sample['comparison']=index
     quiet=not sample['external'] and sample['other_process_cpu_percent']<=runner.inputs['host_quiet_limit_other_process_cpu_percent']
     consecutive=consecutive+1 if quiet else 0;sample['consecutive_quiet']=consecutive;log.write(json.dumps(sample)+'\n');log.flush();time.sleep(1)
   with (f/(name+'-driver.log')).open('a') as log:child=subprocess.Popen(['python3','-B','-u',str(f/'run.py'),'measure',index],stdin=subprocess.DEVNULL,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
   (f/(name+'.pid')).write_text(str(child.pid)+'\n');code=child.wait();child=None
   if code!=75:break
   time.sleep(5)
  (f/(name+'.exit')).write_text(str(code)+'\n')
  if code!=0:(f/'sequence.exit').write_text(str(code)+'\n');raise RuntimeError((name,code))
  print('Completefixed20pairgroup',index,utc(),flush=True);time.sleep(10)
 (f/'sequence.exit').write_text('0\n')
finally:
 if child is not None and child.poll() is None:
  child.terminate()
  try:child.wait(timeout=30)
  except subprocess.TimeoutExpired:child.kill();child.wait()
