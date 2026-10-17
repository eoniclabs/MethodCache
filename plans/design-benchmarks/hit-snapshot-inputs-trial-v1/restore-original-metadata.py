from pathlib import Path
import json,shutil,hashlib,sys
source=Path(__file__).resolve().parent
out=Path(sys.argv[1]);assert not out.exists();shutil.copytree(source/'raw',out)
p=out/'prepared.json';d=json.loads(p.read_text())
for family,entries in d['binaries'].items():d['binaries'][family]={r['path']:r['sha256'] for r in entries}
p.write_text(json.dumps(d,indent=2)+'\n')
rows=json.loads((out/'completed-evidence-index.json').read_text())['files']
for r in rows:assert hashlib.sha256((out/r['path']).read_bytes()).hexdigest()==r['sha256'],r['path']
print('Verified',len(rows),'original file hashes')
