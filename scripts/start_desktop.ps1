# Build the portable GPU engine, then launch the desktop UI.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
cargo build --locked --manifest-path (Join-Path $root 'engine/Cargo.toml') -p ccp_bridge --release
if ($LASTEXITCODE -ne 0) { throw 'Worker build failed' }
$env:CCP_ENGINE_EXE = Join-Path $root 'engine/target/release/ccp-engine.exe'
$env:CCP_NEW_UI_ROOT = $root
if (-not $env:ORT_DYLIB_PATH) { throw 'Set ORT_DYLIB_PATH to the packaged GPU ONNX runtime before launching.' }
dotnet run --project (Join-Path $root 'app/ColorCorrect.App/ColorCorrect.App.csproj')
