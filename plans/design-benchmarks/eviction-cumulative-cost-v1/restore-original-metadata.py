from pathlib import Path
import shutil,json,hashlib,sys
source=Path(__file__).resolve().parent
out=Path(sys.argv[1]);assert not out.exists();shutil.copytree(source/'raw',out)
p=out/'inputs.json';d=json.loads(p.read_text())
for s in d['sources'].values():s['binaries']={r['path']:r['sha256'] for r in s['binaries']}
p.write_text(json.dumps(d,indent=2)+'\n')
rows=json.loads((out/'index.json').read_text())['files']
for r in rows:assert hashlib.sha256((out/r['path']).read_bytes()).hexdigest()==r['sha256'],r['path']
print('Verified',len(rows),'original file hashes')
