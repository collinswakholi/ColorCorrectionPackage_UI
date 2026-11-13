"""Smoke-test the public Studio downloads on their native platforms."""
from pathlib import Path
import hashlib
import json
import os
import plistlib
import subprocess
import sys
import time
from urllib.request import urlopen

from native_ci_acceptance import clean_environment

root = Path(__file__).resolve().parents[1]
rid = sys.argv[1]
work = Path(os.environ['RUNNER_TEMP']) / 'released-studio'
work.mkdir()
os.chdir(work)
base = 'https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/download/v0.3.0/'
sums = {line.split()[1]: line.split()[0] for line in urlopen(base + 'SHA256SUMS').read().decode().splitlines()}
def fetch(name):
    target = work / name
    subprocess.run(['curl', '--fail', '--location', '--retry', '4', '--silent', '--show-error', base + name, '-o', target], check=True)
    with target.open('rb') as stream:
        assert hashlib.file_digest(stream, 'sha256').hexdigest() == sums[name], name
    return target
manifest = json.loads(fetch(f'package-manifest-{rid}.json').read_text())
assert manifest['version'] == '0.3.0' and manifest['source']['git_commit'] == 'ef114d63af1094c949f3d81d71ccb03dc7f40240'
def run(*args, **kwargs):
    subprocess.run(args, check=True, **kwargs)
if rid.startswith('win'):
    setup = fetch(f'ColorCorrect-Studio-0.3.0-{rid}-Setup.exe')
    payload = work / 'installed'
    run(setup, '/VERYSILENT', '/SUPPRESSMSGBOXES', '/CURRENTUSER', '/NORESTART', '/COMPONENTS=app,python', '/TASKS=', f'/DIR={payload}')
    app = payload / 'app/ColorCorrect.App.exe'
elif rid.startswith('linux'):
    arch = 'amd64' if rid == 'linux-x64' else 'arm64'
    run('sudo','apt-get','install','-y',fetch(f'colorcorrect-studio_0.3.0_{arch}.deb'))
    payload = Path('/opt/colorcorrect-studio')
    app = payload / 'app/ColorCorrect.App'
else:
    run('ditto','-x','-k',fetch('ColorCorrect-Studio-0.3.0-osx-arm64.zip'),work)
    contents = work / 'ColorCorrect Studio.app/Contents'
    payload = contents / 'Resources/runtime'
    app = contents / 'MacOS' / plistlib.loads((contents / 'Info.plist').read_bytes())['CFBundleExecutable']
    run('codesign','--verify','--deep','--strict',contents.parent)
run(sys.executable,'-m','venv','test-env')
python = work / ('test-env/Scripts/python.exe' if os.name == 'nt' else 'test-env/bin/python')
wheel, = (payload / 'python').glob('colorcorrect_studio-2.3.0-*.whl')
run(python,'-m','pip','install','--no-index','--no-deps',wheel)
env = clean_environment(payload,rid)
fixtures = root / 'testdata/acceptance'
for name, expected in json.loads((fixtures/'manifest.json').read_text()).items():
    assert hashlib.sha256((fixtures/name).read_bytes()).hexdigest() == expected
for policy in ('cpu-inference','auto'):
    run(python,'-I',root/'scripts/acceptance_cpu_workflow.py','--package',payload,'--fixtures',fixtures,'--output',work/policy,'--backend',policy,env=env)
    result = json.loads((work/policy/'result.json').read_text())
    assert result['status']=='passed' and result['sdk_version']=='2.3.0'
proc = subprocess.Popen([str(app)], cwd=work, env=env)
try:
    time.sleep(10)
    assert proc.poll() is None, proc.returncode
finally:
    if proc.poll() is None:
        proc.terminate()
        proc.wait(timeout=30)
print(f'Passed released {rid} application launch and both SDK image workflows.')
