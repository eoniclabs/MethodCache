"""Verify stored proof bytes and reconstruct the original source-hash metadata."""
from pathlib import Path
import hashlib,json,shutil,sys
root=Path(__file__).resolve().parent
out=Path(sys.argv[1]);out.mkdir()
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
m=json.loads((root/'manifest.json').read_text())
for row in m['files']:
 p=root/row['path'];assert sha(p)==row['sha256'],row['path']
 q=out/row['path'];q.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(p,q)
 if row.get('original_sha256'):
  x=json.loads(q.read_text())
  for point,entries in x['production_source_files'].items():x['production_source_files'][point]={e['path']:e['sha256'] for e in entries}
  q.write_text(json.dumps(x,indent=2)+'\n');assert sha(q)==row['original_sha256'],row['path']
print('All stored files verify; both original preflight source catalogs restored byte-for-byte')
