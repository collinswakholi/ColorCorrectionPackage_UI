# Building and installing from source

Use .NET SDK **10.0.402**, Rust **1.95.0**, and Python **3.10+** for SDK/tests. The SDK and Rust pins are in `global.json` and `rust-toolchain.toml`. Windows builds also need the Visual Studio C++ tools and Windows SDK. The application needs a compatible graphics driver for detection; CPU correction does not provide CPU detection.

## Windows

```powershell
python scripts/fetch_gpu_runtime.py --rid win-x64 --output dist/gpu-runtime
$env:ORT_DYLIB_PATH = (Resolve-Path dist/gpu-runtime/onnxruntime.dll).Path
cargo build --manifest-path engine/Cargo.toml --locked --release -p ccp_bridge
dotnet build app/ColorCorrect.sln -c Release
pwsh scripts/start_desktop.ps1
```

Use `win-arm64` and a native ARM64 toolchain on Windows ARM64. Runtime downloads verify pinned hashes and remain app-local. Installer preparation and prerequisite options are described in [installer documentation](../installer/README.md).

## Linux

Build on the target architecture with a C/C++ linker and the distribution's GTK, fontconfig, X11/XWayland and Vulkan loader dependencies. Install the GPU vendor's driver using the distribution or vendor-supported route. In WSL, the Windows host supplies the GPU driver; do not install a Linux kernel GPU driver over it.

```sh
python3 scripts/fetch_gpu_runtime.py --rid linux-x64 --output dist/gpu-runtime
export ORT_DYLIB_PATH="$PWD/dist/gpu-runtime/libonnxruntime.so"
export LD_LIBRARY_PATH="$PWD/dist/gpu-runtime:${LD_LIBRARY_PATH:-}"
cargo build --manifest-path engine/Cargo.toml --locked --release -p ccp_bridge
dotnet build app/ColorCorrect.sln -c Release
```

Use `linux-arm64` on native ARM64. See the installer guide for portable archives, Debian packages and dependency checks. A build alone does not establish native GPU acceptance.

## Apple-silicon macOS: source only

Use macOS 14 or later on Apple silicon. These instructions are prepared for native validation; no Mac host has yet executed them for this candidate. No macOS installer, application binary or runtime archive is distributed.

Install [Apple's command-line tools](https://developer.apple.com/documentation/xcode/installing-the-command-line-tools/), the **Arm64 .NET SDK 10.0.402** using [Microsoft's macOS instructions](https://learn.microsoft.com/en-us/dotnet/core/install/macos), [Rust through rustup](https://rust-lang.github.io/rustup/installation/), and a native Python 3.10+ interpreter from [Python's macOS downloads](https://www.python.org/downloads/macos/). The Python universal2 distribution can run natively on Apple silicon. Open a native Terminal rather than a Rosetta shell. From the source checkout, verify:

```sh
xcode-select --install
xcode-select -p
uname -m
dotnet --version
dotnet --info
rustup toolchain install 1.95.0-aarch64-apple-darwin --profile minimal
rustc +1.95.0-aarch64-apple-darwin --version
python3 -c 'import platform; print(platform.machine())'
```

`uname` and Python must report `arm64`; .NET must report the Arm64 architecture and the pinned SDK. If the command-line tools are already installed, `xcode-select --install` may report that instead of opening its installation dialog.

Choose a new local build directory; the helper fetches runtime inputs whose package hashes are pinned in `scripts/fetch_gpu_runtime.py`. A checksum mismatch aborts the download. Build-time internet access is required for uncached native runtimes, Rust crates and NuGet packages:

```sh
mkdir -p dist
bash scripts/build_macos.sh "$PWD/dist/local-macos"
"$PWD/dist/local-macos/launch"
```

The helper creates a self-contained local application and a launch script. Its private build root contains the worker, verified runtime, and model/reference snapshots, so both the GUI launcher and an explicit SDK root use the same files. A later build in the source checkout cannot replace this build's worker. The desktop application does not need Python after building. Native app/GPU acceptance remains outstanding. Intel macOS is outside scope. No signing or notarization is performed.

Install the Python API in a local virtual environment and point it at that local build:

```sh
python3 -m venv .venv-macos
. .venv-macos/bin/activate
python -m pip install ./api
python - <<'PY'
from pathlib import Path
from colorcorrect import Client

source = Path.cwd()
build = source / "dist/local-macos"
with Client(root=build) as client:
    calibration = client.calibrate(
        source / "test_images/ColorCard_Big.JPG",
        output_dir=source / "dist/local-macos/example-results",
    )
    result = calibration.apply(
        source / "test_images/ColorCard_Big.JPG",
        output_dir=source / "dist/local-macos/example-corrected",
    )
    print(result.outputs)
PY
```

Use an absolute `Client(root=...)` path when starting Python from another directory. The private runtime stays beside its selected worker; an incomplete build rejects borrowing an unrelated runtime. Calibration requires available GPU inference; selecting CPU correction still requires GPU chart detection.

If a build fails, check native toolchain architecture and the versions above first. A missing `.dylib` indicates an incomplete runtime download or wrong build root; use a fresh output directory and inspect `dist/local-macos/gpu-runtime-manifest.json`. A runtime checksum failure must be investigated rather than bypassed. If no hardware adapter initializes, retain the diagnostics and treat GPU acceptance as failed; CPU correction cannot replace neural inference. After moving the local build directory, rebuild into a new output directory so the generated launch script captures the correct paths. Do not disable Gatekeeper globally to work around a launch error.

To remove the local SDK, run `python -m pip uninstall colorcorrect-studio` while its virtual environment is active, then `deactivate`. After closing the app, remove only your chosen `dist/local-macos` and `.venv-macos` folders in Finder; preserve exported models, image outputs and sessions that you want to keep. Shared Xcode, .NET, Rust and Python installations are managed separately and are not removed by this procedure.

## Component tests and SDK

Use CPython **3.12.10** for the CI/workflow contract suite. Its pinned, hashed
PyYAML wheel is a build/test tool; the desktop app and SDK gain no runtime
dependency. The listed wheels cover the five native CI targets.

```text
python -m pip install --only-binary=:all: --require-hashes -r scripts/requirements-ci.txt
dotnet test app/ColorCorrect.sln -c Release -p:RestoreLockedMode=true
cargo test --release --manifest-path engine/Cargo.toml --workspace --lib --tests --locked
python -m unittest discover -s api/tests -p test_*.py
python -m unittest discover -s scripts/tests -p test_*.py
python scripts/export_source.py --check
python -m pip install ./api
```

For this validation effort, component/unit checks run on the Windows host; the extracted final application and installed wheel are exercised in Sandbox and WSL. GitHub Actions builds native Windows/Linux x64 and ARM64 packages; actual hardware GPU acceptance is recorded separately. [Validation split](native-validation.md)

The SDK discovers an intact native package with `Client(root=...)` or `ColorCorrection(..., root=...)`. It never downloads GPU drivers. No server or Python ML package is needed.

## Reproducible source export

```text
python scripts/export_source.py --output _implementation/release-readiness/source-export
```

The destination must not exist. The export includes current unstaged/untracked source, hashes every copied file, rejects links/private artifacts and excludes local experiments/caches. It records actual Git HEAD/dirty state when this is a checkout; Git metadata remains unavailable for a plain source archive instead of claiming an old revision. Build and test that export from another working directory before calling it ready for GitHub.
