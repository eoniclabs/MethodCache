"""Stop only our V5 controller after its worker and owned cleanup have completed."""
from pathlib import Path
import datetime
import hashlib
import json
import os
import signal
import time

folder = Path(__file__).resolve().parent
previous = folder.parent/'guid-final-v5'
assert (folder.parent/'OWNER').read_text().strip() == 'methodcache-design-20261015-01a13a93'
receipt = folder/'stop-at-idle.json'
assert not receipt.exists(), 'Preserve the original stop receipt'
pid = int((previous/'timing-driver.pid').read_text().strip())
proc = Path(f'/proc/{pid}')
expected = str(previous/'timing-all.py').encode()
deadline = time.monotonic()+50
while time.monotonic() < deadline:
    assert expected in (proc/'cmdline').read_bytes(), 'Original process identity changed'
    children = proc/'task'/str(pid)/'children'
    lines = (previous/'timing-driver.log').read_text().splitlines()
    last = lines[-1] if lines else ''
    if last.startswith('Finished ') and ' exit 0 ' in last and not children.read_text().strip():
        # Freeze the parent before checking again, closing the next-spawn race.
        os.kill(pid,signal.SIGSTOP)
        try:
            time.sleep(.05)
            if children.read_text().strip() or (previous/'timing-driver.log').read_text().splitlines()[-1] != last:
                continue
            record = {'at_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
                'controller_pid':pid,'controller_command':(proc/'cmdline').read_bytes().replace(b'\0',b' ').decode(),
                'idle_children':[],'last_driver_line':last,
                'original_metadata_sha256':hashlib.sha256((previous/'timing-results/metadata.json').read_bytes()).hexdigest(),
                'reason':'Source-scoped schedule and cumulative-first ordering, selected without inspecting timing means',
                'other_jobs_interrupted':False}
            os.kill(pid,signal.SIGTERM)
            receipt.write_text(json.dumps(record,indent=2)+'\n')
            print('Stopped owned V5 controller at successful idle boundary:',last,flush=True)
            break
        finally:
            try: os.kill(pid,signal.SIGCONT)
            except ProcessLookupError: pass
    time.sleep(.2)
else:
    print('No idle boundary yet; original controller and active batch remain running.',flush=True)
