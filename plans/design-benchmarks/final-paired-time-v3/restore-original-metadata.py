"""Recreate original archive metadata and verify all four complete source indexes."""
from pathlib import Path
import hashlib,json,shutil,sys
source=Path(__file__).resolve().parent
target=Path(sys.argv[1]);assert not target.exists()
total=0
for name in ['raw','native','additional']:
 shutil.copytree(source/name,target/name)
for p in target.rglob('prepared.json'):
 d=json.loads(p.read_text())
 for family,rows in d['binaries'].items():
  if isinstance(rows,list):d['binaries'][family]={r['path']:r['sha256'] for r in rows}
 p.write_text(json.dumps(d,indent=2)+'\n')
for p in target.rglob('completed-evidence-index.json'):
 d=json.loads(p.read_text());f=d['files']
 rows=f.items() if isinstance(f,dict) else ((r['path'],r['sha256']) for r in f)
 for name,expected in rows:
  assert hashlib.sha256((p.parent/name).read_bytes()).hexdigest()==expected,name
  total+=1
print('Restored and verified',total,'original archived file hashes')
