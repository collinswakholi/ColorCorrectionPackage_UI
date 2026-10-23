# Native installation candidates

The desktop application includes a self-contained .NET runtime, the Rust worker,
three detector models and architecture-matched GPU inference libraries. Python is
optional and is required only for the separate SDK. System GPU drivers belong to
the operating system/vendor and are never replaced by these installers.

On native Windows 11 x64 or ARM64, stage pinned runtimes and the offline Microsoft
VC prerequisite, then build the matching package:

```powershell
python scripts/fetch_gpu_runtime.py --rid win-x64 --output artifacts/runtime --vc-redist-output artifacts/prerequisites
./scripts/package_windows.ps1 -Rid win-x64 -Runtime artifacts/runtime -VcRedist artifacts/prerequisites/vc_redist.x64.exe -InnoCompiler 'C:\Path\To\ISCC.exe' -FetchNotices
```

The installer is per-user. Its optional SDK component copies a wheel and examples;
it does not install Python. A missing VC runtime produces an explicit opt-in
installation task using the included signed Microsoft installer. Only that task
requests elevation. Silent installs never elevate or silently repair prerequisites.
Portable ZIP users run `check-system.ps1`; `-InstallPrerequisites` opts into repair.

On native Ubuntu 24.04 x64 or ARM64:

```sh
python3 scripts/fetch_gpu_runtime.py --rid linux-x64 --output artifacts/runtime
CCP_ORT_RUNTIME="$PWD/artifacts/runtime" bash scripts/package_linux.sh --fetch-notices
```

Outputs are a `.deb` with explicit system-library dependencies and a portable
archive. Filenames include the native app version, for example
`ColorCorrect-Studio-0.2.0-win-x64-Setup.exe`,
`ColorCorrect-Studio-0.2.0-linux-arm64.tar.gz` and
`colorcorrect-studio_0.2.0_arm64.deb`. The optional SDK wheel has its own version.
`sudo apt install ./package.deb` lets the OS resolve those dependencies.
The build's explicit notice-fetch option obtains missing license text only from
the exact upstream commits declared by active dependencies. Offline builds can
pass a validated `--dependency-notices` inventory collected previously.
Portable users can inspect `install-dependencies` and explicitly run it with
`--install`. Neither route changes GPU drivers. Launchers check prerequisites using
OS tools before invoking the application/worker; they do not require Python.

The experimental WSL x64 add-on is optional and defaults off. Build it privately
with `scripts/build_wsl_addon.sh NEW_DIRECTORY`, then pass its `addon` folder to
`package_native.py --wsl-addon`. Its Mesa 25.2.8 driver has a relative ICD path and
recorded source hashes, build settings, dependencies and notices. The worker alone
activates it after `wsl-gpu-setup enable`, or with explicit
`CCP_WSL_GPU_ADDON=1`; `disable` removes the preference. No global ICD, GPU vendor
selection, system driver, or GUI graphics environment is changed. WSL's host
`libd3d12.so`, `libdxcore.so` and `/dev/dxg` remain prerequisites. Add-on rebuild
reproducibility and each new artifact's hardware correctness must be validated.

Apple-silicon macOS is source-only. Use `scripts/build_macos.sh`; the source CI job
uploads text evidence only. There is no Mac installer, binary archive or Intel Mac
target. See the repository installation guide for prerequisites.

CI uses native architectures and pinned actions, builds unsigned candidates, and
performs separate extracted-archive and installed-package checks. Hosted runner
labels do not establish hardware GPU or complete UI acceptance. The optional
hardware job requires a provisioned runner and an explicit dispatch opt-in. Native
UI, hardware, clean installation, redistribution review and signing gates remain
visible in package/evidence manifests. No workflow publishes a GitHub release.
