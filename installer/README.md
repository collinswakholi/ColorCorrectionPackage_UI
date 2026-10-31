# Install ColorCorrect Studio

Windows, Ubuntu and Apple-silicon macOS downloads include the application
runtime, engine, detector models and matching inference libraries. Python is optional. The installers do
not replace system graphics drivers.

Choose the package for your operating system and architecture. The downloads are
not publisher-signed; Mac bundles use ad-hoc signing and are not notarized.
ARM64 and WSL graphics workflows remain experimental.
See [compatibility](../docs/compatibility.md) for platform requirements.

Version **0.2.2** includes explicit CPU detection, Auto inference recovery and
device-aware setup. Previous release assets remain unchanged. Use the SDK wheel
from your chosen application package to keep its worker and API together.

## Windows

Run the matching Setup installer, then open **ColorCorrect Studio**. Installation
is per user. The optional Python SDK component copies a wheel and examples; it
does not install Python.

If Microsoft Visual C++ runtime files are missing, Setup offers the included
Microsoft prerequisite installer. This step is optional and requests elevation.
Declining it leaves the prerequisite unresolved; complete it before launching the
app. Silent installation does not elevate or repair prerequisites automatically.
Follow any restart instructions from the Microsoft installer.

For a portable installation, extract the complete ZIP and run
`ColorCorrect-Studio.cmd`. Keep every file in the extracted folder. You can run
`check-system.ps1` to inspect prerequisites; `-InstallPrerequisites` explicitly
opts into their installation. `setup.ps1 -Diagnose -Verify` checks installed CPU/GPU
engines; `setup.ps1 -Mode cpu -Install` applies CPU preferences after confirmation.
Setup offers Recommended, CPU only, and Custom choices. CPU mode omits optional
WebGPU files while retaining shared libraries needed to load CPU inference.
Rerun the matching installer with Custom to add omitted acceleration components.

## Ubuntu 24.04

Install the Debian package using its actual downloaded filename:

```sh
sudo apt install ./colorcorrect-studio_0.2.2_amd64.deb
colorcorrect-studio
```

Use the `_arm64.deb` file on ARM64. The package manager supplies required system
libraries; offline installation needs those libraries already installed or cached.

For a portable installation, extract the archive, run `./check-system`, then
launch `./ColorCorrect-Studio`. To install missing Ubuntu libraries, inspect and
run `./install-dependencies --install`. `./setup --diagnose --verify` checks device
inventory and actual engine execution. `./setup --mode cpu --install` chooses CPU
operation; `--mode custom --components webgpu` selects optional support. Setup
previews changes and asks for confirmation; package maintainer scripts do not
run interactive setup or nested apt commands. System-library installation is a separate,
explicit action. Neither installation route changes graphics drivers.

## Python SDK

The optional wheel is in the application's `python/` folder. On macOS this folder
is inside `ColorCorrect Studio.app/Contents/Resources/runtime`. Install it into your Python
environment, then select that native root with `root=...`. The wheel
contains neither the native engine nor its models. See the [Python guide](../api/README.md)
for installation and examples.

## Experimental WSL GPU setup

Official Linux downloads do not include the Mesa Dozen add-on needed for GPU
inference in the current WSL setup. A separately published add-on installer is
not available. Enabling a setting alone cannot supply the missing files.

The optional x64 add-on is built separately and starts disabled. When it is
included in your application folder, `wsl-gpu-setup enable` activates it for worker
processes; `wsl-gpu-setup disable` clears that preference. It does not change the
GUI's graphics environment, a global graphics-driver setting or the selected GPU
vendor. Use the Windows host's supported graphics driver, not a Linux kernel GPU
driver inside WSL. CPU inference and CPU correction work without the add-on.

For source preparation, see the [developer packaging instructions](../docs/development/README.md#packaging).

## Apple-silicon macOS

Download the `osx-arm64` ZIP or tar archive, extract **ColorCorrect Studio.app**,
and move the bundle to Applications. macOS 14+ and Apple silicon are required.
The app includes its private dependencies and does not require Python or a .NET SDK.

Bundles use ad-hoc signing and are not Developer ID signed or notarized. Follow
the [Mac installation guide](../docs/macos-install.md) for checksums, first-launch
security prompts and optional SDK installation. [Source build instructions](../docs/building.md#apple-silicon-macos-source-build)
remain available. Intel macOS is outside scope.

## Checksums and notices

Use the checksums supplied with your download to confirm its files. Each
architecture also has a package manifest. Keep the runtime and model notices with
the application; the project MIT license does not override their separate terms.
See [third-party notices](../THIRD_PARTY_NOTICES.md) and [model provenance](../docs/model-provenance.md).

Build and release procedures belong in the [developer guide](../docs/development/README.md)
and [release guide](../docs/releasing.md).
