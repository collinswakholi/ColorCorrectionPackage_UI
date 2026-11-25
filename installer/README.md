# Install ColorCorrect Studio

Version **0.5.0**. The Windows, Linux and Apple-silicon macOS downloads contain
the application runtime, engine, detection models and inference libraries.
Python is optional, and the installers never replace system graphics drivers.

The downloads are not publisher-signed; Mac bundles are ad-hoc signed and not
notarized. ARM64, macOS and WSL graphics workflows are experimental. See
[compatibility](../docs/compatibility.md) for platform requirements.

## Windows

Run the matching Setup installer, then open **ColorCorrect Studio**. Installation
is per user. Setup offers **Recommended**, **CPU only** and **Custom**; CPU only
leaves out the optional WebGPU files. Run Setup again with Custom to add omitted
components. The optional Python SDK component copies the wheel and examples; it
does not install Python.

If the Microsoft Visual C++ runtime is missing, Setup offers the included
Microsoft installer, which asks for elevation. If you decline, install the
runtime before launching the app. Silent installation does not elevate or install
prerequisites.

For the portable version, extract the complete ZIP and run
`ColorCorrect-Studio.cmd`. Keep every file in the extracted folder.

| Command | What it does |
| --- | --- |
| `check-system.ps1` | Lists missing prerequisites; add `-InstallPrerequisites` to install them |
| `setup.ps1 -Diagnose -Verify` | Checks the installed CPU and GPU engines |
| `setup.ps1 -Mode cpu -Install` | Switches to CPU operation after confirmation |

## Ubuntu and Debian

Ubuntu 24.04 and 22.04 and Debian 13 and 12, x64 or ARM64 (glibc 2.35 or newer).
One package installs on all four:

```sh
sudo apt install ./colorcorrect-studio_0.5.0_amd64.deb
colorcorrect-studio
```

Use the `_arm64.deb` file on ARM64. The package declares its system libraries, so
apt installs them; an offline install needs them already installed or cached.

For the portable archive (`ColorCorrect-Studio-0.5.0-linux-x64.tar.gz` or
`-linux-arm64.tar.gz`), extract it and run:

```sh
./check-system                     # lists every missing library and the install command
./install-dependencies --install   # add --yes for unattended installs
./ColorCorrect-Studio
```

`check-system` also finds libraries the app loads at runtime (X11, GTK, OpenGL,
ICU, OpenSSL), which `ldd` cannot see. If a library is still missing at start-up,
the app shows a readable message; after a launch from the desktop menu the report
is in `~/.config/colorcorrect/startup-error.txt`.

| Command | What it does |
| --- | --- |
| `./setup --diagnose --verify` | Checks the device inventory and runs each engine |
| `./setup --mode cpu --install` | Switches to CPU operation |
| `./setup --mode custom --components webgpu` | Selects optional GPU support |

Setup previews its changes and asks for confirmation. Package maintainer scripts
never run interactive setup or nested apt commands, and neither route changes
graphics drivers.

## Apple-silicon macOS

Download the `osx-arm64` ZIP or tar archive, extract **ColorCorrect Studio.app**
and move it to Applications. macOS 14 or later on Apple silicon is required; the
app needs no Python or .NET SDK. Intel Macs are not supported.

The [Mac installation guide](../docs/macos-install.md) covers checksums, the
first-launch security prompt and the optional SDK. To build the app yourself, see
[building from source](../docs/building.md#apple-silicon-macos-source-build).

## Python SDK

The optional wheel is in the application's `python/` folder (on macOS, inside
`ColorCorrect Studio.app/Contents/Resources/runtime`). Install it into your Python
environment and pass that application folder as `root=...`. The wheel contains
neither the native engine nor its models. See the [Python guide](../api/README.md).

## Experimental WSL GPU setup

GPU inference under WSL needs the Mesa Dozen add-on, which official Linux
downloads do not include and which is not published separately. Enabling a
setting cannot supply the missing files. CPU inference and correction work
without it.

The optional x64 add-on is built separately and starts disabled. When it is in
your application folder, `wsl-gpu-setup enable` turns it on for the engine
processes and `wsl-gpu-setup disable` turns it off. It does not change the GUI's
graphics environment, a global driver setting or the GPU vendor. Use the Windows
host's graphics driver, not a Linux kernel GPU driver inside WSL. To build the
add-on, see the [developer packaging instructions](../docs/development/README.md#packaging).

## Checksums and notices

Each release has `SHA256SUMS-<rid>.txt` and `package-manifest-<rid>.json` for
every platform; use them to confirm your download. Keep the runtime and model
notices with the application: the project's MIT license does not override their
terms. See [third-party notices](../THIRD_PARTY_NOTICES.md) and
[model provenance](../docs/model-provenance.md).

Build and release procedures are in the [developer guide](../docs/development/README.md)
and the [release guide](../docs/releasing.md).
