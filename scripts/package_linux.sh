#!/usr/bin/env bash
# Build a portable Linux x64 archive. Run this script natively on Linux x86_64.
set -Eeuo pipefail
IFS=$'\n\t'

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
DIST="$ROOT/dist/linux-x64"
PUBLISH="$DIST/publish"
STAGE="$DIST/stage"
PACKAGE_NAME="ColorCorrect-Studio-linux-x64"
PACKAGE="$STAGE/$PACKAGE_NAME"
ARCHIVE="$DIST/$PACKAGE_NAME.tar.gz"
CACHE="$ROOT/.build-cache/linux-x64"
LIBTORCH="${CCP_LIBTORCH_SDK:-$CACHE/sdk/libtorch}"
LIBTORCH_VERSION="${LIBTORCH_VERSION:-2.9.1}"
LIBTORCH_SHA256="9D0CF62DB9B72DE5B27071A945D6DE4F340BFFD996C3DD770EEB3F8237D05E75"
ORT_VERSION="${ORT_VERSION:-1.22.0}"
ORT_SHA256="8344D55F93D5BC5021CE342DB50F62079DAF39AAAFB5D311A451846228BE49B3"

die() { printf 'error: %s\n' "$*" >&2; exit 1; }
require_cmd() { command -v "$1" >/dev/null 2>&1 || die "required tool not found: $1"; }
download() {
  curl --fail --location --retry 3 --retry-delay 2 --proto '=https' --tlsv1.2 "$1" -o "$2"
}
verify_sha256() {
  local actual
  actual="$(sha256sum "$1" | awk '{print toupper($1)}')"
  [[ "$actual" == "$2" ]] || die "SHA-256 mismatch for $1"
}
safe_clean() {
  local resolved
  resolved="$(realpath -m "$1")"
  case "$resolved" in "$ROOT/dist/"*) rm -rf -- "$resolved" ;; *) die "refusing to clean $resolved" ;; esac
}
guard_libtorch_path() {
  local resolved
  resolved="$(realpath -m "$LIBTORCH")"
  [[ "$resolved" != "/" && "$resolved" != "$HOME" && "$(basename "$resolved")" == "libtorch" ]] ||
    die "CCP_LIBTORCH_ROOT must be a dedicated path ending in /libtorch"
}
require_asset() { [[ -f "$ROOT/$1" ]] || die "missing release asset: $1"; }

[[ "$(uname -s)" == "Linux" ]] || die "run package_linux.sh on Linux"
[[ "$(uname -m)" == "x86_64" ]] || die "Linux package target is x86_64 (found $(uname -m))"
[[ "$LIBTORCH_VERSION" == "2.9.1" ]] || die "no reviewed LibTorch hash for $LIBTORCH_VERSION"
for tool in cargo curl dotnet file realpath sha256sum tar unzip; do require_cmd "$tool"; done
guard_libtorch_path
for asset in \
  models/opt2/best_assemble_fp16_1280.torchscript \
  models/opt2/best_assemble_fp16_1536.torchscript \
  models/opt2/best_assemble_fp32_1280.onnx \
  models/opt2/best_assemble_fp32_1536.onnx \
  models/plane/plane_det_model_YOLO_512_n.onnx \
  models/apply/lut3d_trilinear.onnx \
  refs/neutrals.json; do require_asset "$asset"; done

mkdir -p "$CACHE" "$DIST"
if [[ ! -d "$LIBTORCH" ]]; then
  [[ -z "${CCP_LIBTORCH_SDK:-}" ]] || die "explicit SDK does not exist"
  archive="$CACHE/libtorch-linux-x64-$LIBTORCH_VERSION.zip"
  download "https://download.pytorch.org/libtorch/cpu/libtorch-shared-with-deps-${LIBTORCH_VERSION}%2Bcpu.zip" "$archive"
  verify_sha256 "$archive" "$LIBTORCH_SHA256"
  extract="$CACHE/sdk"
  [[ ! -e "$extract" ]] || die "incomplete isolated SDK; move it aside first"
  mkdir -p "$extract"
  unzip -q "$archive" -d "$extract"
  printf '{"kind":"cpu","version":"%s"}\n' "$LIBTORCH_VERSION" > "$LIBTORCH/libtorch.json"
fi
[[ -f "$LIBTORCH/include/torch/csrc/api/include/torch/torch.h" ]] || die "full compilation SDK required, not runtime-only tree"
grep -Eq '"kind"[[:space:]]*:[[:space:]]*"cpu"' "$LIBTORCH/libtorch.json" || die "LibTorch tree is not CPU"
grep -Eq '"version"[[:space:]]*:[[:space:]]*"2\.9\.1"' "$LIBTORCH/libtorch.json" || die "LibTorch tree is not version 2.9.1"

ORT_ARCHIVE="$CACHE/onnxruntime-linux-x64-$ORT_VERSION.tgz"
ORT_ROOT="$CACHE/onnxruntime-linux-x64-$ORT_VERSION"
if [[ ! -f "$ORT_ROOT/lib/libonnxruntime.so.$ORT_VERSION" ]]; then
  download "https://github.com/microsoft/onnxruntime/releases/download/v$ORT_VERSION/onnxruntime-linux-x64-$ORT_VERSION.tgz" "$ORT_ARCHIVE"
  verify_sha256 "$ORT_ARCHIVE" "$ORT_SHA256"
  rm -rf -- "$ORT_ROOT"
  tar -xzf "$ORT_ARCHIVE" -C "$CACHE"
fi

export LIBTORCH="$LIBTORCH" LIBTORCH_PATH="$LIBTORCH" KMP_DUPLICATE_LIB_OK=TRUE LIBTORCH_BYPASS_VERSION_CHECK=1
export LD_LIBRARY_PATH="$LIBTORCH/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
(cd "$ROOT/engine" && cargo build --locked -p ccp_bridge --release --features tch,ort-plane)
file "$ROOT/engine/target/release/ccp-engine" | grep -q 'ELF 64-bit.*x86-64' || die "worker is not a Linux x64 executable"

safe_clean "$PUBLISH"
safe_clean "$STAGE"
mkdir -p "$PUBLISH" "$PACKAGE/app" "$PACKAGE/models/opt2" "$PACKAGE/models/plane" "$PACKAGE/models/apply"
dotnet publish "$ROOT/app/ColorCorrect.App/ColorCorrect.App.csproj" \
  -c Release -r linux-x64 --self-contained true --nologo \
  -p:UseAppHost=true -p:PublishSingleFile=false -p:PublishReadyToRun=true \
  -p:DebugType=none -p:DebugSymbols=false -o "$PUBLISH"
cp -a "$PUBLISH/." "$PACKAGE/app/"
cp "$ROOT/engine/target/release/ccp-engine" "$PACKAGE/ccp-engine"
mkdir -p "$PACKAGE/libtorch/lib"
cp -a "$LIBTORCH/lib/"*.so* "$PACKAGE/libtorch/lib/"
cp "$LIBTORCH/libtorch.json" "$PACKAGE/libtorch/"
cp "$ORT_ROOT/lib/"libonnxruntime.so* "$PACKAGE/"
cp -a "$ROOT/refs" "$PACKAGE/refs"
(cd "$ROOT" && tar --exclude=__pycache__ --exclude='*.pyc' --exclude=.ipynb_checkpoints --exclude=.venv -cf - api docs notebooks test_images licenses README.md LICENSE THIRD_PARTY_NOTICES.md) | tar -xf - -C "$PACKAGE"
cp "$ROOT/models/opt2/"* "$PACKAGE/models/opt2/"
cp "$ROOT/models/plane/plane_det_model_YOLO_512_n.onnx" "$PACKAGE/models/plane/"
cp "$ROOT/models/apply/"* "$PACKAGE/models/apply/"
cp "$ROOT/models/manifest.json" "$PACKAGE/models/"
cp "$ROOT/models/NOTICE.md" "$ROOT/models/LICENSE-AGPL-3.0.txt" "$PACKAGE/models/"
chmod +x "$PACKAGE/ccp-engine" "$PACKAGE/app/ColorCorrect.App"

cat > "$PACKAGE/run.sh" <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
export CCP_NEW_UI_ROOT="$HERE" CCP_ENGINE_EXE="$HERE/ccp-engine"
export CCP_LIBTORCH_ROOT="$HERE/libtorch" LIBTORCH="$HERE/libtorch" LIBTORCH_PATH="$HERE/libtorch"
export KMP_DUPLICATE_LIB_OK=TRUE
export LD_LIBRARY_PATH="$HERE:$HERE/libtorch/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec "$HERE/app/ColorCorrect.App" "$@"
EOF
chmod +x "$PACKAGE/run.sh"
cat > "$PACKAGE/README.txt" <<'EOF'
ColorCorrect Studio for Linux x64

Run ./run.sh to process still images. Video capabilities are not included.

The .NET runtime, ColorCorrect worker, ONNX Runtime, models, references, and CPU LibTorch are bundled.
The package targets glibc-based x86_64 desktop Linux.
EOF
(cd "$PACKAGE" && find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS)
rm -f -- "$ARCHIVE" "$ARCHIVE.sha256"
tar -C "$STAGE" -czf "$ARCHIVE" "$PACKAGE_NAME"
sha256sum "$ARCHIVE" > "$ARCHIVE.sha256"
printf 'Wrote %s\n' "$ARCHIVE"
