#!/usr/bin/env bash
# Build a native macOS arm64 .app archive. Run this script on Apple Silicon.
set -Eeuo pipefail
IFS=$'\n\t'

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
DIST="$ROOT/dist/macos-arm64"
PUBLISH="$DIST/publish"
STAGE="$DIST/stage"
APP="$STAGE/ColorCorrect Studio.app"
MACOS="$APP/Contents/MacOS"
RESOURCES="$APP/Contents/Resources"
ARCHIVE="$DIST/ColorCorrect-Studio-macos-arm64.tar.gz"
CACHE="$ROOT/.build-cache/macos-arm64"
LIBTORCH="${CCP_LIBTORCH_SDK:-$CACHE/sdk/libtorch}"
LIBTORCH_VERSION="${LIBTORCH_VERSION:-2.9.1}"
LIBTORCH_SHA256="C469E516B5D970EEDA294AABD1F16D9A0368931C3EED30263746F7FBA7CD9B96"
ORT_VERSION="${ORT_VERSION:-1.22.0}"
ORT_SHA256="CAB6DCBD77E7EC775390E7B73A8939D45FEC3379B017C7CB74F5B204C1A1CC07"
APP_VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$ROOT/app/ColorCorrect.App/ColorCorrect.App.csproj" | head -n1)"

die() { printf 'error: %s\n' "$*" >&2; exit 1; }
require_cmd() { command -v "$1" >/dev/null 2>&1 || die "required tool not found: $1"; }
download() { curl --fail --location --retry 3 --retry-delay 2 --proto '=https' --tlsv1.2 "$1" -o "$2"; }
verify_sha256() {
  local actual
  actual="$(shasum -a 256 "$1" | awk '{print toupper($1)}')"
  [[ "$actual" == "$2" ]] || die "SHA-256 mismatch for $1"
}
resolved_path() { python3 -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$1"; }
safe_clean() {
  local resolved
  resolved="$(resolved_path "$1")"
  case "$resolved" in "$ROOT/dist/"*) rm -rf -- "$resolved" ;; *) die "refusing to clean $resolved" ;; esac
}
guard_libtorch_path() {
  local resolved
  resolved="$(resolved_path "$LIBTORCH")"
  [[ "$resolved" != "/" && "$resolved" != "$HOME" && "$(basename "$resolved")" == "libtorch" ]] ||
    die "CCP_LIBTORCH_ROOT must be a dedicated path ending in /libtorch"
}
require_asset() { [[ -f "$ROOT/$1" ]] || die "missing release asset: $1"; }

[[ "$(uname -s)" == "Darwin" ]] || die "run package_macos.sh on macOS"
[[ "$(uname -m)" == "arm64" ]] || die "macOS package target is arm64 (found $(uname -m))"
[[ -n "$APP_VERSION" ]] || die "could not read app version"
[[ "$LIBTORCH_VERSION" == "2.9.1" ]] || die "no reviewed LibTorch hash for $LIBTORCH_VERSION"
for tool in cargo codesign curl dotnet file iconutil plutil python3 sed shasum sips tar unzip; do require_cmd "$tool"; done
guard_libtorch_path
for asset in \
  app/ColorCorrect.App/Assets/app.png \
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
  archive="$CACHE/libtorch-macos-arm64-$LIBTORCH_VERSION.zip"
  download "https://download.pytorch.org/libtorch/cpu/libtorch-macos-arm64-$LIBTORCH_VERSION.zip" "$archive"
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

ORT_ARCHIVE="$CACHE/onnxruntime-osx-arm64-$ORT_VERSION.tgz"
ORT_ROOT="$CACHE/onnxruntime-osx-arm64-$ORT_VERSION"
if [[ ! -f "$ORT_ROOT/lib/libonnxruntime.dylib" ]]; then
  download "https://github.com/microsoft/onnxruntime/releases/download/v$ORT_VERSION/onnxruntime-osx-arm64-$ORT_VERSION.tgz" "$ORT_ARCHIVE"
  verify_sha256 "$ORT_ARCHIVE" "$ORT_SHA256"
  rm -rf -- "$ORT_ROOT"
  tar -xzf "$ORT_ARCHIVE" -C "$CACHE"
fi

export LIBTORCH="$LIBTORCH" LIBTORCH_PATH="$LIBTORCH" KMP_DUPLICATE_LIB_OK=TRUE LIBTORCH_BYPASS_VERSION_CHECK=1
export DYLD_LIBRARY_PATH="$LIBTORCH/lib${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}"
(cd "$ROOT/engine" && cargo build --locked -p ccp_bridge --release --features tch,ort-plane,video)
file "$ROOT/engine/target/release/ccp-engine" | grep -q 'Mach-O 64-bit executable arm64' || die "worker is not a macOS arm64 executable"

safe_clean "$PUBLISH"
safe_clean "$STAGE"
mkdir -p "$PUBLISH" "$MACOS/models/opt2" "$MACOS/models/plane" "$MACOS/models/apply" "$RESOURCES"
dotnet publish "$ROOT/app/ColorCorrect.App/ColorCorrect.App.csproj" \
  -c Release -r osx-arm64 --self-contained true --nologo \
  -p:UseAppHost=true -p:PublishSingleFile=false -p:PublishReadyToRun=true \
  -p:DebugType=none -p:DebugSymbols=false -o "$PUBLISH"
cp -a "$PUBLISH/." "$MACOS/"
cp "$ROOT/engine/target/release/ccp-engine" "$MACOS/ccp-engine"
mkdir -p "$MACOS/libtorch/lib"
cp -a "$LIBTORCH/lib/"*.dylib "$MACOS/libtorch/lib/"
cp "$LIBTORCH/libtorch.json" "$MACOS/libtorch/"
cp "$ORT_ROOT/lib/"libonnxruntime*.dylib "$MACOS/"
cp -a "$ROOT/refs" "$MACOS/refs"
(cd "$ROOT" && tar --exclude=__pycache__ --exclude='*.pyc' --exclude=.ipynb_checkpoints --exclude=.venv -cf - api docs notebooks test_images licenses README.md LICENSE THIRD_PARTY_NOTICES.md) | tar -xf - -C "$MACOS"
cp "$ROOT/models/opt2/"* "$MACOS/models/opt2/"
cp "$ROOT/models/plane/plane_det_model_YOLO_512_n.onnx" "$MACOS/models/plane/"
cp "$ROOT/models/apply/lut3d_trilinear.onnx" "$MACOS/models/apply/"
chmod +x "$MACOS/ColorCorrect.App" "$MACOS/ccp-engine"

ICONSET="$DIST/ColorCorrect.iconset"
safe_clean "$ICONSET"
mkdir -p "$ICONSET"
for spec in '16 icon_16x16.png' '32 icon_16x16@2x.png' '32 icon_32x32.png' '64 icon_32x32@2x.png' \
            '128 icon_128x128.png' '256 icon_128x128@2x.png' '256 icon_256x256.png' \
            '512 icon_256x256@2x.png' '512 icon_512x512.png' '1024 icon_512x512@2x.png'; do
  size="${spec%% *}"; name="${spec#* }"
  sips -z "$size" "$size" "$ROOT/app/ColorCorrect.App/Assets/app.png" --out "$ICONSET/$name" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$RESOURCES/ColorCorrect.icns"
safe_clean "$ICONSET"

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleExecutable</key><string>ColorCorrect.App</string>
  <key>CFBundleIdentifier</key><string>com.collinswakholi.colorcorrectstudio</string>
  <key>CFBundleName</key><string>ColorCorrect</string>
  <key>CFBundleDisplayName</key><string>ColorCorrect Studio</string>
  <key>CFBundleIconFile</key><string>ColorCorrect.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>$APP_VERSION</string>
  <key>CFBundleShortVersionString</key><string>$APP_VERSION</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
EOF
plutil -lint "$APP/Contents/Info.plist"
ENTITLEMENTS="$DIST/ColorCorrect.entitlements"
cat > "$ENTITLEMENTS" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>com.apple.security.cs.allow-jit</key><true/>
</dict></plist>
EOF
while IFS= read -r -d '' native; do
  if [[ "$native" != "$MACOS/ColorCorrect.App" ]] && file "$native" | grep -q 'Mach-O'; then
    codesign --force --sign - "$native"
  fi
done < <(find "$APP/Contents" -type f -print0)
codesign --force --sign - --entitlements "$ENTITLEMENTS" "$MACOS/ColorCorrect.App"
codesign --force --sign - --entitlements "$ENTITLEMENTS" "$APP"
codesign --verify --strict --verbose=2 "$APP"

cat > "$STAGE/README.txt" <<'EOF'
ColorCorrect Studio for Apple Silicon

Move ColorCorrect Studio.app to Applications and open it. CPU LibTorch, ONNX Runtime,
the native worker, models, references, and the .NET runtime are bundled.

Video support requires FFmpeg. Install it with: brew install ffmpeg

This development archive is ad-hoc signed, not Developer ID notarized. If Gatekeeper
blocks it after download, run: xattr -cr "ColorCorrect Studio.app"
EOF
rm -f -- "$ARCHIVE" "$ARCHIVE.sha256"
tar -C "$STAGE" -czf "$ARCHIVE" "ColorCorrect Studio.app" README.txt
shasum -a 256 "$ARCHIVE" > "$ARCHIVE.sha256"
printf 'Wrote %s\n' "$ARCHIVE"
