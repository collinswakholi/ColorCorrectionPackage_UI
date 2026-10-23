#!/usr/bin/env bash
# Explicit build operation only; no apt, sudo, driver installation or global ICD.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
[[ "$(uname -m)" == x86_64 ]] || { echo 'WSL add-on is currently x64 only.' >&2; exit 1; }
source /etc/os-release
[[ "$ID" == ubuntu && "$VERSION_ID" == 24.04 ]] || { echo 'Build in Ubuntu 24.04.' >&2; exit 1; }
BASE="${1:?Usage: build_wsl_addon.sh NEW_BUILD_DIRECTORY}"
mkdir "$BASE"
BASE="$(cd "$BASE" && pwd -P)"
for tool in curl tar git cc c++ pkg-config ninja bison flex glslangValidator python3 readelf; do
  command -v "$tool" >/dev/null || { echo "Missing build tool: $tool" >&2; exit 1; }
done
curl --fail --location --retry 3 https://archive.mesa3d.org/mesa-25.2.8.tar.xz -o "$BASE/mesa.tar.xz"
echo '097842f3e49d996868b38688db87b006f7d4541e93ce86d2f341d8b3e7be7c93  mesa.tar.xz' | (cd "$BASE" && sha256sum --check --strict)
tar -xJf "$BASE/mesa.tar.xz" -C "$BASE"
git clone --filter=blob:none --no-checkout https://github.com/microsoft/DirectX-Headers.git "$BASE/headers"
git -C "$BASE/headers" checkout --detach c94b9b23aaadc2034dd1cad656a5a69f1526f98a
python3 -m venv "$BASE/tools"
"$BASE/tools/bin/python" -m pip install 'meson==1.9.1' 'Mako==1.3.10' 'MarkupSafe==3.0.4' 'packaging==25.0' 'PyYAML==6.0.2'
export PATH="$BASE/tools/bin:$PATH"
meson setup "$BASE/headers-build" "$BASE/headers" --prefix="$BASE/prefix" --buildtype=release --wrap-mode=nodownload -Dbuild-test=false
ninja -C "$BASE/headers-build" install
export PKG_CONFIG_PATH="$BASE/prefix/lib/x86_64-linux-gnu/pkgconfig:$BASE/prefix/lib/pkgconfig"
meson setup "$BASE/mesa-build" "$BASE/mesa-25.2.8" --prefix="$BASE/prefix" --buildtype=release --wrap-mode=nodownload \
  -Dvulkan-drivers=microsoft-experimental -Dgallium-drivers= -Dplatforms= -Dglx=disabled -Degl=disabled \
  -Dopengl=false -Dgles1=disabled -Dgles2=disabled -Dllvm=disabled -Dvideo-codecs= -Dbuild-tests=false
ninja -C "$BASE/mesa-build" -j "${CCP_BUILD_JOBS:-2}"
ninja -C "$BASE/mesa-build" install
python3 - "$BASE" <<'PY'
import json, subprocess, sys
from pathlib import Path
base=Path(sys.argv[1])
def output(*args): return subprocess.check_output(args,text=True).strip()
record={'mesa_sha256':'097842f3e49d996868b38688db87b006f7d4541e93ce86d2f341d8b3e7be7c93',
 'headers_commit':output('git','-C',str(base/'headers'),'rev-parse','HEAD'),
 'os_release':Path('/etc/os-release').read_text(), 'compiler':output('cc','--version'),
 'packages':output('dpkg-query','-W'), 'meson_options':(base/'mesa-build/meson-private/cmd_line.txt').read_text(),
 'python_tools':output(str(base/'tools/bin/python'),'-m','pip','freeze'),
 'build_image':__import__('os').environ.get('CCP_BUILD_IMAGE_DIGEST','unrecorded; reproducibility gate pending')}
(base/'build-provenance.json').write_text(json.dumps(record,indent=2))
PY
python3 "$ROOT/scripts/package_wsl_addon.py" --prefix "$BASE/prefix" --mesa-source "$BASE/mesa-25.2.8" \
 --headers-source "$BASE/headers" --build-provenance "$BASE/build-provenance.json" --output "$BASE/addon"
