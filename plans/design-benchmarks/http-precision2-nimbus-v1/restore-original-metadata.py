from pathlib import Path
import hashlib,json,sys
root=Path(__file__).resolve().parent;out=Path(sys.argv[1]);out.mkdir();sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest();d=json.loads((root/'manifest.json').read_text())
for row in d['files']:
 p=root/row['path'];assert sha(p)==row['sha256'],row['path'];q=out/row['path'];q.parent.mkdir(parents=True,exist_ok=True);q.write_bytes(p.read_bytes())
 if row.get('catalogue_paths'):
  data=json.loads(q.read_text())
  for parts in reversed(row['catalogue_paths']):
   parent=data
   for part in parts[:-1]:parent=parent[part]
   rows=parent[parts[-1]]if parts else data;restored={r['path']:r['sha256']for r in rows}
   if parts:parent[parts[-1]]=restored
   else:data=restored
  q.write_text(json.dumps(data,indent=2)+'\n');assert sha(q)==row['original_sha256'],row['path']
print('Every stored byte verified; original canonical metadata reconstructed byte-for-byte.')
