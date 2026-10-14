"""Fail closed on unsafe public source, changed weights, or dirty tutorial output."""
from pathlib import Path
import hashlib
import json
import re
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
files = subprocess.check_output(['git', 'ls-files', '-z', '--cached', '--others', '--exclude-standard'], cwd=root).decode().split('\0')
failures = []
private = re.compile(r'(?:[A-Za-z]:[\\/]+Users[\\/]+C[o]llins[\\/]|[A-Za-z]:\\\\Users\\\\C[o]llins\\\\|/Users/C[o]llins/|-----BEGIN (?:[A-Z0-9]+ )?PRIVATE KEY-----[\r\n]+[A-Za-z0-9+/]{32})', re.I)
generated = {'bin', 'obj', 'target', '__pycache__', '.build-cache', 'TestResults', 'dist', '.avalonia-build-tasks'}
text_suffixes = {'.md','.py','.rs','.cs','.json','.ipynb','.ps1','.sh','.yml','.yaml','.toml','.iss','.txt','.props','.csproj','.cff','.psd1'}
for relative in sorted(set(filter(None, files))):
    path = root / relative
    if not path.exists():  # staged legacy removals
        continue
    if generated.intersection(path.relative_to(root).parts):
        failures.append(f'generated output tracked: {relative}')
    if path.is_symlink() or (hasattr(path, 'is_junction') and path.is_junction()):
        failures.append(f'link/junction: {relative}')
    if path.stat().st_size >= 100 * 1024 * 1024:
        failures.append(f'GitHub individual-file limit: {relative}')
    if path.suffix.lower() in {'.exe','.dll','.pfx','.key','.pyc','.pkl'}:
        failures.append(f'binary/private artifact: {relative}')
    if path.suffix.lower() in text_suffixes and private.search(path.read_text(encoding='utf-8-sig', errors='replace')):
        failures.append(f'private path/key: {relative}')
    if path.suffix == '.ipynb':
        for cell in json.loads(path.read_text(encoding='utf-8-sig'))['cells']:
            if cell.get('outputs') or cell.get('execution_count') is not None:
                failures.append(f'saved notebook execution: {relative}')

manifest = json.loads((root / 'models/manifest.json').read_text())
actual_models = {p.relative_to(root).as_posix() for p in (root/'models').rglob('*') if p.is_file() and p.name != 'manifest.json'}
if actual_models != {f['path'] for f in manifest['files']}:
    failures.append('model manifest does not cover exactly the shipped weights')
for item in manifest['files']:
    path = root / item['path']
    with path.open('rb') as stream:
        checksum = hashlib.file_digest(stream, 'sha256').hexdigest()
    if path.stat().st_size != item['bytes'] or checksum != item['sha256']:
        failures.append(f'changed model: {item["path"]}')
expected = {'Blank.JPG','ColorCard_Big.JPG','ColorCard_Combo.JPG','Sample_3572.JPG','Sample_3573.JPG'}
if {p.name for p in (root/'test_images').iterdir()} != expected:
    failures.append('public corpus must contain exactly the five original images')
if failures:
    print('\n'.join(failures), file=sys.stderr)
    raise SystemExit(1)
print(f'PASS: public-source scan, five samples and {len(manifest["files"])} pinned model artifacts')
