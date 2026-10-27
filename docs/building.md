# Building and installing from source

Use .NET SDK **10.0.402**, Rust **1.95.0**, and Python **3.10+** for the build helpers and optional SDK. The version pins are in `global.json` and `rust-toolchain.toml`. Windows also needs the Visual Studio C++ tools and Windows SDK. Chart detection requires a compatible GPU driver; CPU correction can apply an existing model.

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

Use `linux-arm64` on native ARM64. See the [installer guide](../installer/README.md) for portable archives, Debian packages and system dependencies. ARM64 graphics and desktop workflows remain experimental; see the [platform notes](compatibility.md).

## Apple-silicon macOS: source only

Use macOS 14 or later on Apple silicon. Build and run the application locally;
no macOS installer, application binary or runtime archive is distributed.
Graphics and desktop compatibility are not yet confirmed on representative Mac
hardware. See the [platform notes](compatibility.md) before relying on a Mac
build for production work.

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

The helper creates a self-contained local application and a launch script. Its private build root contains the worker, verified runtime, and model/reference snapshots, so both the GUI launcher and an explicit SDK root use the same files. A later build in the source checkout cannot replace this build's worker. The desktop application does not need Python after building. Mac graphics and desktop workflows remain experimental. Intel macOS is outside scope. No signing or notarization is performed.

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

If a build fails, check native toolchain architecture and the versions above first. A missing `.dylib` indicates an incomplete runtime download or wrong build root; use a fresh output directory and inspect `dist/local-macos/gpu-runtime-manifest.json`. A runtime checksum failure must be investigated rather than bypassed. If no hardware adapter initializes, chart and plane detection are unavailable. Review the graphics diagnostics; CPU correction can still apply a saved model. After moving the local build directory, rebuild into a new output directory so the generated launch script captures the correct paths. Do not disable Gatekeeper globally to work around a launch error.

To remove the local SDK, run `python -m pip uninstall colorcorrect-studio` while its virtual environment is active, then `deactivate`. After closing the app, remove only your chosen `dist/local-macos` and `.venv-macos` folders in Finder; preserve exported models, image outputs and sessions that you want to keep. Shared Xcode, .NET, Rust and Python installations are managed separately and are not removed by this procedure.

## Python SDK

Install the optional SDK from the source checkout with `python -m pip install ./api`.
Use `Client(root=...)` or `ColorCorrection(..., root=...)` to select an intact
native application folder. The SDK never downloads graphics drivers and needs no
server or Python ML packages. See the [API guide](../api/README.md) for examples.

## Distribution and development

Windows and Linux have installers and portable packages. Release assets include
checksums and package manifests. Packages remain unsigned; Apple-silicon macOS
remains source only. See [installation details](../installer/README.md) for
prerequisites and [release procedures](releasing.md) for maintainers.

The [developer guide](development/README.md) covers automated builds, component
checks and manifest-based source exports.
