from pathlib import Path
import subprocess,time,datetime
folder=Path(__file__).resolve().parent
for mode in [['prepare'],['measure','7']]:
 while True:
  with (folder/('driver-'+mode[0]+'.log')).open('a') as log:
   p=subprocess.Popen(['python3','-B','-u',str(folder/'run.py'),*mode],stdin=subprocess.DEVNULL,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
   (folder/(mode[0]+'.pid')).write_text(str(p.pid)+'\n');code=p.wait()
  if code!=75:break
  time.sleep(5)
 (folder/(mode[0]+'.exit')).write_text(str(code)+'\n')
 if code:raise SystemExit(code)
(folder/'sequence.exit').write_text('0\n')
print('Candidate Mac cumulative complete',datetime.datetime.now(datetime.timezone.utc).isoformat(),flush=True)
