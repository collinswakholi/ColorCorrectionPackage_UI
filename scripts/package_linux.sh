#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
: "${CCP_ORT_RUNTIME:?Set CCP_ORT_RUNTIME to a native ONNX Runtime plus WebGPU provider directory}"
cargo build --locked --manifest-path "$ROOT/engine/Cargo.toml" -p ccp_bridge --release
case "$(uname -m)" in x86_64) rid=linux-x64 ;; aarch64) rid=linux-arm64 ;; *) echo 'Unsupported native architecture' >&2; exit 1 ;; esac
python3 "$ROOT/scripts/package_native.py" --rid "${CCP_RID:-$rid}" --worker "$ROOT/engine/target/release/ccp-engine" --ort-runtime "$CCP_ORT_RUNTIME" --deb "$@"
