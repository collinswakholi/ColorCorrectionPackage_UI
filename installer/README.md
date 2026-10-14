# ColorCorrect Studio installers

## Windows (CPU in Setup; CUDA on demand)

1. `pwsh scripts/fetch_ffmpeg.ps1` — sidecar `ffmpeg.exe` + `ffprobe.exe` into `installer/` (not committed).
2. `pwsh scripts/package_windows.ps1 -BuildInstaller` — publishes the app, stages **CPU** LibTorch DLLs to `dist/libtorch-cpu`, writes `dist/ColorCorrect-Studio-win-x64-portable.zip`, and invokes Inno Setup 6. Omit `-BuildInstaller` when only the portable package is needed. Does **not** embed CUDA.
3. Output: `dist/ColorCorrect-Studio-win-x64.exe` plus `.sha256` sidecars for the installer and portable ZIP.
4. Wizard **Acceleration** page (CPU unless NVIDIA is detected):
   - **CPU** — extracted from Setup.
   - **NVIDIA CUDA** — reuse a local LibTorch 2.9 CUDA tree if found (`%LOCALAPPDATA%\CCP-New\libtorch`, `CCP_LIBTORCH_ROOT` / `LIBTORCH_PATH`, or the download cache). Otherwise prompt to download official `2.9.1+cu126` (~3.8 GB) via `scripts/install_libtorch_cuda.ps1`. The app repeats that offer on first run if Setup stayed on CPU.
5. Intel / AMD / NVIDIA (without CUDA LibTorch): Setup ships ONNX Runtime DirectML next to `ccp-engine` for **chart detect**, **plane crop**, and **CC LUT apply** when the DirectML policy is selected or Auto picks it. Opt2 ONNX (`best_assemble_fp32_*.onnx`) and `lut3d_trilinear.onnx` ship in `models/`. NVIDIA with CUDA LibTorch still uses LibTorch for detect/apply.
6. Silent: `ColorCorrect-Studio-win-x64.exe /SILENT` and `/LIBTORCH=cuda` to take the CUDA path.
7. Unsigned: SmartScreen will warn. See `SMARTSCREEN.txt`.

`best_assemble_fp32_*.onnx` and `lut3d_trilinear.onnx` are included (~80 MB uncompressed for the ONNX pair).

Do **not** run Setup on a machine whose live `libtorch` you want to keep unless you intend to replace that folder (CPU choice overwrites a CUDA tree).

## Windows CUDA add-on (optional)

For an already-installed CPU tree without using the wizard download:

1. `pwsh scripts/fetch_libtorch.ps1 -Kind cuda -StageOnly` or `install_libtorch_cuda.ps1`
2. Compile `installer/cuda_addon.iss`
3. The add-on **deletes and replaces** the single `libtorch_root`

## macOS arm64 CPU

Run `bash scripts/package_macos.sh` natively on Apple Silicon. It writes `dist/macos-arm64/ColorCorrect-Studio-macos-arm64.tar.gz` containing a proper ad-hoc-signed `ColorCorrect Studio.app`, bundled CPU runtime, model assets, and icon. Move the app to Applications. Install FFmpeg with Homebrew for video support. This is not a Developer ID notarized release; use `xattr -cr "ColorCorrect Studio.app"` only if Gatekeeper quarantines the development build. No CUDA add-on.

## Linux x64 CPU

Run `bash scripts/package_linux.sh` natively on x86_64 Linux. It writes `dist/linux-x64/ColorCorrect-Studio-linux-x64.tar.gz` with CPU LibTorch and all application assets bundled. Install your distribution's FFmpeg package, then run `./run.sh`. An explicit `CCP_LIBTORCH_ROOT` can still override the bundled runtime for advanced deployments.

## VC++ redistributable

Self-contained .NET 9 includes its own runtime. If `ccp-engine` / LibTorch asks for VC++ 2019–2022 x64, install the official Microsoft redistributable on the target PC.
