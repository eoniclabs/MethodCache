"""Executed by flock: preserve the lease through bounded child exit and owned cleanup."""
import subprocess,sys,datetime
label=sys.argv[1]
print('Acquired exclusive lease',label,datetime.datetime.now(datetime.timezone.utc).isoformat(),flush=True)
code=1
try:
 result=subprocess.run(['timeout','--signal=TERM','--kill-after=20s','1470s',*sys.argv[2:]])
 code=result.returncode
finally:
 # Exact label only; never prune shared Docker state. No credentials are printed.
 found=subprocess.run(['docker','ps','-aq','--filter','label=methodcache-design-bench='+label],capture_output=True,text=True,check=True)
 ids=found.stdout.split()
 if ids:subprocess.run(['timeout','--kill-after=5s','10s','docker','rm','--force',*ids],check=True)
 print('Releasing exclusive lease',label,'exit',code,datetime.datetime.now(datetime.timezone.utc).isoformat(),flush=True)
raise SystemExit(code)
