# Building and validating source

Windows prerequisites: Windows 11 x64, PowerShell 7, .NET SDK **10.0.402**, Rust
**1.95.0**, Visual Studio 2022 C++ build tools + Windows SDK + licensed VC redist,
Python 3.10+ for SDK tests, and Inno Setup 6 for the installer. `global.json` and
`rust-toolchain.toml` select the tested versions; dependency lockfiles are committed.
Provision tools before building; do not substitute whatever happens to be on PATH.

```powershell
pwsh ./scripts/link_samples.ps1 # validates tracked samples; creates no junctions
pwsh ./scripts/fetch_libtorch.ps1 -Kind cpu -StageOnly
pwsh ./scripts/fetch_onnxruntime.ps1 -Dest .build-cache/test-runtime
pwsh ./scripts/start_desktop.ps1
```

`fetch_libtorch` verifies a pinned official archive and extracts a **full compilation
SDK** under `.build-cache/sdk/`. It stages **runtime-only DLLs** under
`dist/libtorch-cpu`. Runtime downloads cannot replace a developer's installed
CPU/CUDA runtime. `CCP_LIBTORCH_SDK` may point to a complete 2.9.1 SDK for an
explicit development override; a DLL-only runtime cannot compile `torch-sys`.
Release validation uses the pinned artifact, not this override.

```powershell
dotnet restore app/ColorCorrect.sln --locked-mode
dotnet test app/ColorCorrect.sln -c Release --no-restore
python -m unittest discover -s api/tests -v
python scripts/check_repository.py

$env:LIBTORCH = (Get-Content .build-cache/sdk-cpu.json -Raw | ConvertFrom-Json).path
$env:LIBTORCH_PATH = $env:LIBTORCH
$env:LIBTORCH_BYPASS_VERSION_CHECK = '1'
$env:PATH = "$env:LIBTORCH/lib;$env:PATH"
$env:ORT_DYLIB_PATH = (Resolve-Path .build-cache/test-runtime/onnxruntime.dll).Path
cargo fmt --manifest-path engine/Cargo.toml --all -- --check
cargo clippy --manifest-path engine/Cargo.toml --workspace --all-targets --all-features --locked -- -D warnings
cargo test --manifest-path engine/Cargo.toml --workspace --all-targets --all-features --locked
```

Run worker integration and notebooks **against the staged application**, not a
different developer binary:

```powershell
pwsh ./scripts/package_windows.ps1 -BuildInstaller -SkipPortableArchive
$env:COLORCORRECT_ROOT = (Resolve-Path dist/win-cpu).Path
$env:CCP_NEW_UI_ROOT = $env:COLORCORRECT_ROOT
$env:CCP_ENGINE_EXE = "$env:COLORCORRECT_ROOT/ccp-engine.exe"
$env:CCP_LIBTORCH_ROOT = "$env:COLORCORRECT_ROOT/libtorch"
python api/tests/integration_workflow.py --root $env:COLORCORRECT_ROOT
python api/tests/integration_no_video.py --root $env:COLORCORRECT_ROOT
python api/tests/integration_all_methods.py --root $env:COLORCORRECT_ROOT
python -m pip install -r notebooks/requirements-qa.txt
python api/tests/execute_notebooks.py --root $env:COLORCORRECT_ROOT
```

Artifacts and executed notebooks belong in ignored test/output directories, not
source. Never regenerate all visual baselines to turn a failure green: inspect
each actual/diff image and approve only explained layout/content changes.

Set `COLORCORRECT_DATA_DIR` to an isolated writable profile for live QA. This
redirects native session/settings and preview-cache storage; the default remains
the existing `CCP-New` data folder. It does not relocate installed CUDA add-ons.
Do not use your real session as a destructive test fixture.

Packaging fetches checksum-verified ORT DirectML 1.22.1 and DirectML 1.15.4.
The worker builds with `tch,ort-plane`; there is no video feature or media-sidecar
download. `integration_no_video.py` verifies that former video API operations
fail explicitly while supported still-image operations remain available.
VC runtime DLLs come from the licensed Visual Studio redistributable
directory, never arbitrary System32 copies. A source commit and dependency inventory
are recorded in `build-info.json`; `SHA256SUMS.txt` covers payload files.
Reproducibility means locked inputs and auditable provenance, not a promise that
Inno/.NET builds are byte-identical across machines or build times.

Linux/macOS scripts require their native target host and the pinned toolchains.
They retain CPU LibTorch 2.9.1 / ORT 1.22.0 and use isolated build SDKs.
They also build a still-image-only worker and do not depend on host FFmpeg.
Only Windows has a v0.1.0 preview release workflow.
