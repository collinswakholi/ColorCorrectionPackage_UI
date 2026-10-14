"""Verify payload integrity, runtime locality, sample corpus and source provenance."""
import argparse
import hashlib
import json
from pathlib import Path
import re

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('payload', type=Path)
parser.add_argument('--release', action='store_true')
parser.add_argument('--installed', action='store_true', help='Allow only Inno-created root uninstaller files')
args = parser.parse_args()
root = args.payload.resolve()
required = ['ccp-engine.exe','app/ColorCorrect.App.exe','app/coreclr.dll','libtorch/lib/torch_cpu.dll',
            'onnxruntime.dll','DirectML.dll','ffmpeg.exe','ffprobe.exe','vcruntime140.dll','msvcp140.dll',
            'api/colorcorrect.py','docs/user-guide.md','docs/images/workflow.svg',
            'notebooks/01_headless_workflow.ipynb','notebooks/02_compare_fit_methods.ipynb',
            'test_images/Blank.JPG','test_images/ColorCard_Big.JPG','test_images/ColorCard_Combo.JPG',
            'test_images/Sample_3572.JPG','test_images/Sample_3573.JPG','build-info.json',
            'licenses/dependency-inventory.json','licenses/ffmpeg/LICENSE','licenses/ffmpeg/README.txt',
            'licenses/dotnet/LICENSE.txt','licenses/pytorch-2.9.1/LICENSE.txt','THIRD_PARTY_NOTICES.md']
for name in required:
    assert (root/name).is_file(), f'Missing payload file: {name}'
covered = set()
for line in (root/'SHA256SUMS.txt').read_text(encoding='utf-8-sig').splitlines():
    expected, name = line.split('  ', 1)
    path = root/name
    assert path.is_relative_to(root) and not path.is_symlink(), name
    with path.open('rb') as stream:
        assert hashlib.file_digest(stream, 'sha256').hexdigest() == expected.lower(), name
    covered.add(name)
actual = {p.relative_to(root).as_posix() for p in root.rglob('*') if p.is_file() and p.name != 'SHA256SUMS.txt' and '__pycache__' not in p.parts}
if args.installed:
    actual = {p for p in actual if not re.fullmatch(r'unins\d{3}\.(exe|dat|msg)', p)}
assert covered == actual, 'Unmanifested payload files'
private = re.compile(r'(?:[A-Za-z]:[\\/]+Users[\\/]+C[o]llins[\\/]|[A-Za-z]:\\\\Users\\\\C[o]llins\\\\|/Users/C[o]llins/|PRIVATE\s+KEY-----)', re.I)
for p in root.rglob('*'):
    if p.is_file() and p.suffix.lower() in {'.json','.md','.txt','.ipynb','.py','.ps1','.psd1'}:
        assert not private.search(p.read_text(encoding='utf-8-sig', errors='replace')), f'Private payload path/key: {p.relative_to(root)}'
    elif p.is_file() and p.suffix.lower() in {'.exe','.dll'}:
        # Chunked scan includes C++ assertion strings as well as Rust file paths.
        with p.open('rb') as stream:
            tail = b''
            while block := stream.read(1024 * 1024):
                data = tail + block
                assert not private.search(data.decode('latin-1')), f'Private binary path: {p.relative_to(root)}'
                tail = data[-256:]
info = json.loads((root/'build-info.json').read_text(encoding='utf-8-sig'))
assert info['version'] == '0.1.0' and info['dotnet_sdk'] == '10.0.402'
if args.release:
    assert not info['dirty_worktree'] and info['release_eligible'], 'Release must match a clean reviewed commit'
print(f'PASS: {len(covered)} payload checksums, app-local runtimes, public examples and provenance')
