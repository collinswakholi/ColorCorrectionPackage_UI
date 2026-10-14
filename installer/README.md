# ColorCorrect Studio Windows preview

See [build instructions](../docs/building.md) and [release gates](../docs/releasing.md).
Build `pwsh scripts/package_windows.ps1 -BuildInstaller -SkipPortableArchive` on
Windows x64 using pinned toolchains and licensed C++/Inno tooling.

Setup version 0.1.0 retains native installer ID
`C0C0A11E-0CC0-4E11-9CC0-000000000001`, distinct from legacy ColorCorrector.
It ships self-contained .NET 10, CPU LibTorch 2.9.1 **app-locally**,
ORT DirectML 1.22.1, DirectML 1.15.4, VC runtime DLLs,
models, references, five public examples, notices, offline guide, SDK and notebooks.
It does not wipe per-user CPU/CUDA libraries or legacy/native user data.
The preview processes still images only; no FFmpeg/ffprobe executable is included.
Reinstall removes only the exact formerly managed FFmpeg/ffprobe binaries,
checksum and their two notice files from the selected native installation folder.
It does not remove a separate media-tool installation or any user files.

CPU is the wizard default even on NVIDIA PCs. CUDA is opt-in; it reuses/downloads
the separate `CCP-New/libtorch-cuda` add-on, never over the bundled CPU runtime.
Silent CPU: `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LIBTORCH=cpu`.
`/LIBTORCH=cuda` explicitly opts into reuse or a multi-GB network download.
Select CPU for offline installation. DirectML requires supported graphics drivers.

The app's explicit environment runtime configuration wins, then valid per-user
CUDA add-on discovery, then bundled CPU. Auto policy may use DirectML with a CPU
LibTorch package. NN training and regression fitting are CPU operations.

Optional `cuda_addon.iss` is retained for development only, not a v0.1.0 release
asset. It targets the separate CUDA folder and refuses an existing destination.
Linux/macOS packaging requires native hosts and is not part of this binary preview.
Uninstall does not remove native `CCP-New` settings/models/sessions or legacy data.
