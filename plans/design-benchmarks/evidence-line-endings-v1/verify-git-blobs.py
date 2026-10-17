"""Verify the complete published evidence batch against Git objects, not only working files."""
from pathlib import Path
import hashlib
import json
import subprocess

root = Path(__file__).resolve().parents[3]
revision = 'f752037547652c3c87b5899efb17733580b72103'
paths = subprocess.check_output(['git', 'diff-tree', '--no-commit-id', '--name-only', '-r', revision + '^', revision], cwd=root, text=True).splitlines()
folders = {Path(p).parent for p in paths if p.startswith('plans/design-benchmarks/') and p.count('/') == 3 and p.endswith('/manifest.json')}
folders.add(Path('plans/design-benchmarks/windows-path-guard-v1'))
expected = {}
for folder in folders:
    manifest = root / folder / 'manifest.json'
    data = json.loads(manifest.read_text())
    rows = data.get('stored_files', data.get('files'))
    if isinstance(rows, dict):
        rows = [{'path': p, 'sha256': h} for p, h in rows.items()]
    for row in rows:
        expected[str(folder / row['path'])] = row.get('stored_sha256', row.get('sha256'))
    for name in ['manifest.json', 'README.md']:
        path = root / folder / name
        if path.is_file():
            expected[str(folder / name)] = hashlib.sha256(path.read_bytes()).hexdigest()
index = {}
for entry in subprocess.check_output(['git', 'ls-files', '-s', '-z'], cwd=root).split(b'\0'):
    if entry:
        info, name = entry.split(b'\t', 1)
        index[name.decode()] = info.split()[1].decode()
names = sorted(expected)
objects = ('\n'.join(index[n] for n in names) + '\n').encode()
contents = subprocess.run(['git', 'cat-file', '--batch'], cwd=root, input=objects, capture_output=True, check=True).stdout
offset = 0
for name in names:
    end = contents.index(b'\n', offset)
    length = int(contents[offset:end].split()[-1])
    blob = contents[end + 1:end + 1 + length]
    offset = end + 1 + length + 1
    assert hashlib.sha256(blob).hexdigest() == expected[name], name
print('Verified', len(names), 'published evidence files and metadata in Git objects across', len(folders), 'proof directories')
