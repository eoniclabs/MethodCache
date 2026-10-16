"""Validate every prepared family without creating or starting a timing series."""
from pathlib import Path
import datetime
import hashlib
import json
import subprocess
import sys

folder = Path(__file__).resolve().parent
root = folder.parent
assert (root/'OWNER').read_text().strip() == 'methodcache-design-20261015-01a13a93'
assert (folder/'completed/inventory').is_file()
assert not (folder/'timing-results').exists(), 'Never substitute preflight for an existing timing attempt'
target = folder/'timing-inputs-validation.json'
assert not target.exists(), 'Preserve previous preflight evidence'
protocol_path = folder/'timing-protocol.json'
prepared_path = folder/'prepared-manifest.json'
protocol_bytes = protocol_path.read_bytes()
prepared_bytes = prepared_path.read_bytes()
protocol = json.loads(protocol_bytes)
prepared = json.loads(prepared_bytes)
assert {name:item['runtime_sha'] for name,item in prepared['sources'].items()} == protocol['revisions']
families = [(name,group) for name in protocol['revisions']
            for group in ('local-contract','local-pressure','native')]
families += [(name,group) for name in prepared['components']
             for group in ('component-lookup','component-facade','component-generated')]
assert len(families) == len(set(families)) == 60
rows = []
for name,group in families:
    assert protocol_path.read_bytes() == protocol_bytes and prepared_path.read_bytes() == prepared_bytes
    command = [sys.executable, str(folder/'timing-worker.py'),'--validate-only',name,group]
    result = subprocess.run(command,capture_output=True,text=True,check=True)
    print(name,group,result.stdout.strip(),flush=True)
    rows.append({'revision':name,'group':group,'exit':result.returncode,'output':result.stdout.strip()})
assert protocol_path.read_bytes() == protocol_bytes and prepared_path.read_bytes() == prepared_bytes
target.write_text(json.dumps({'at_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'protocol_sha256':hashlib.sha256(protocol_bytes).hexdigest(),
    'prepared_manifest_sha256':hashlib.sha256(prepared_bytes).hexdigest(),
    'validated':rows,'measurement_started':False},indent=2)+'\n')
print('All sixty compiled families validated; no timings started.',flush=True)
