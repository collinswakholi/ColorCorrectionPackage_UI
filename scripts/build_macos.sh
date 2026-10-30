#!/usr/bin/env bash
# Local source build, not a redistributable Mac app bundle or archive.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || { echo 'Use native Apple-silicon macOS (not Rosetta).' >&2; exit 1; }
[[ "$(sw_vers -productVersion | cut -d. -f1)" -ge 14 ]] || { echo 'The pinned ONNX runtimes require macOS 14 or later.' >&2; exit 1; }
xcode-select -p >/dev/null
OUT="${1:?Usage: build_macos.sh NEW_OUTPUT_DIRECTORY}"
OUT="$(python3 -c 'from pathlib import Path; import sys; print(Path(sys.argv[1]).expanduser().resolve())' "$OUT")"
# The checked downloader creates a new directory and refuses existing output.
# Runtime and worker must be siblings for explicit-root SDK isolation.
python3 "$ROOT/scripts/fetch_gpu_runtime.py" --rid osx-arm64 --output "$OUT"
RUST_TARGET="$OUT/.build/rust-target"
cargo build --locked --manifest-path "$ROOT/engine/Cargo.toml" --target-dir "$RUST_TARGET" --release -p ccp_bridge
dotnet publish "$ROOT/app/ColorCorrect.App/ColorCorrect.App.csproj" --configuration Release --runtime osx-arm64 \
 --self-contained true -p:RestoreLockedMode=true -p:DebugType=none -p:DebugSymbols=false --output "$OUT/app"
python3 "$ROOT/scripts/stage_macos_source.py" --root "$ROOT" --output "$OUT" --worker "$RUST_TARGET/release/ccp-engine"
echo "Local source build ready: $OUT/launch"
echo "Python API: Client(root=\"$OUT\")"
echo 'This is a local source build, not a supported distribution. Native GPU/UI validation remains required.'
