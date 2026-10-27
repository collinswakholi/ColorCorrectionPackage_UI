# Install ColorCorrect Studio

Windows and Ubuntu downloads include the application runtime, engine, detector
models and matching inference libraries. Python is optional. The installers do
not replace system graphics drivers.

Choose the package for your operating system and architecture. The downloads are
**unsigned previews**; ARM64 and WSL graphics workflows remain experimental.
See [compatibility](../docs/compatibility.md) for platform requirements.

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
opts into their installation.

## Ubuntu 24.04

Install the Debian package using its actual downloaded filename:

```sh
sudo apt install ./colorcorrect-studio_0.2.0_amd64.deb
colorcorrect-studio
```

Use the `_arm64.deb` file on ARM64. The package manager supplies required system
libraries; offline installation needs those libraries already installed or cached.

For a portable installation, extract the archive, run `./check-system`, then
launch `./ColorCorrect-Studio`. To install missing Ubuntu libraries, inspect and
run `./install-dependencies --install`. System-library installation is a separate,
explicit action. Neither installation route changes graphics drivers.

## Python SDK

The optional wheel is in the application's `python/` folder. Install it into your
Python environment, then select the application folder with `root=...`. The wheel
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
driver inside WSL. CPU correction can apply a saved model without the add-on.

For source preparation, see the [developer packaging instructions](../docs/development/README.md#packaging).

## Apple-silicon macOS

macOS is available from source only, with no downloadable application or installer.
Follow the [Apple-silicon build and installation instructions](../docs/building.md#apple-silicon-macos-source-only).
Intel macOS is outside scope.

## Checksums and notices

Use the checksums supplied with your download to confirm its files. Each
architecture also has a package manifest. Keep the runtime and model notices with
the application; the project MIT license does not override their separate terms.
See [third-party notices](../THIRD_PARTY_NOTICES.md) and [model provenance](../docs/model-provenance.md).

Build and release procedures belong in the [developer guide](../docs/development/README.md)
and [release guide](../docs/releasing.md).
