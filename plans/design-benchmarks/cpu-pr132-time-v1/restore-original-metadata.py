"""Restore byte-exact original metadata from the reversible path/hash catalogue representation."""
from pathlib import Path
import hashlib,json,shutil,sys
source=Path(__file__).resolve().parent/'raw'
target=Path(sys.argv[1])
assert not target.exists(),'Choose a new output directory'
shutil.copytree(source,target)
p=target/'prepared.json'
data=json.loads(p.read_text())
for family,rows in data['binaries'].items():
 if isinstance(rows,list):data['binaries'][family]={r['path']:r['sha256'] for r in rows}
p.write_text(json.dumps(data,indent=2)+'\n')
index=json.loads((target/'completed-evidence-index.json').read_text())
for row in index['files']:
 assert hashlib.sha256((target/row['path']).read_bytes()).hexdigest()==row['sha256'],row['path']
print('Restored and verified',len(index['files']),'original file hashes')
