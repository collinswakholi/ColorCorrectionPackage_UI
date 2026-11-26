# Compatibility

ColorCorrect Studio 0.5.1 ships Windows and Linux packages and an Apple-silicon
macOS app bundle. Each download contains its own runtime, models and libraries,
so keep the application folder or bundle intact. Python is optional.

Chart and plane detection on **Auto** try a compatible GPU and fall back to the
CPU. CPU detection and CPU correction support the complete workflow without a
GPU. Installing a different Python wheel does not replace the native engine.

| Platform | Packages | Status |
| --- | --- | --- |
| Windows 11 x64 | Installer and portable ZIP | Supported. CPU inference and correction, with optional DirectML/WebGPU acceleration. |
| Windows 11 ARM64 | Installer and portable ZIP | Experimental; graphics and desktop workflows are not yet confirmed on representative ARM64 hardware. |
| Ubuntu 24.04 and 22.04, Debian 13 and 12 (x64) | Debian package and portable archive | Native desktop and GPU use are experimental. Needs glibc 2.35 or newer. |
| Ubuntu 24.04 and 22.04, Debian 13 and 12 (ARM64) | Debian package and portable archive | Experimental; graphics and desktop workflows are not yet confirmed on representative ARM64 hardware. |
| macOS 14+ (Apple silicon) | App bundle in ZIP and tar archives | Experimental; packaging and CPU checks run in CI, GPU and interactive desktop use are checked separately. |

Linux packages are built on Ubuntu 22.04 (glibc 2.35), so one `.deb` and one
portable archive per architecture serve all four distributions. Every release is
installed on bare images of each of them in CI.

Choose the architecture of your operating system: **x64** for Intel/AMD, **ARM64**
for Arm. There are no 32-bit or Intel macOS builds.
[Download and install](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/README.md#download) or [build from source](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/building.md).

## Graphics requirements

Install graphics drivers through your operating system or GPU vendor; the app
supplies its own libraries and never replaces a system driver. Windows prefers
DirectML for detection; Linux uses WebGPU/Vulkan and macOS WebGPU/Metal.

Image correction has its own **Auto**, **CPU** and **GPU** choice, and the
advanced detection settings offer Auto, CPU and the platform's GPU engines. Auto
can create a calibration without a GPU; a forced GPU engine reports an error
instead of switching engines. See [processing options](compute/README.md).

## WSL

GPU use under WSL is experimental. GPU chart and plane detection need a
separately built Mesa Dozen add-on and working Windows-host GPU support. Official
Linux downloads do not include the add-on, and enabling a setting cannot supply
it. CPU inference and correction work without it.

Follow the [WSL setup instructions](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/installer/README.md#experimental-wsl-gpu-setup).
Use the Windows host's GPU driver; do not install a Linux kernel GPU driver
inside WSL.

## Other limits

- Windows and Linux packages are not publisher-signed. Mac bundles are ad-hoc
  signed, not Developer ID signed or notarized. Check the release source and
  checksums, then follow the [Mac installation guide](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/macos-install.md) for the
  first-launch prompt.
- Some high-DPI layouts, graphics adapters and driver combinations are
  unconfirmed.
- The app processes still images. Reuse a model only for matching camera,
  lighting, exposure and image-encoding conditions.
- Full FFC/GC/WB processing expects encoded sRGB/D65 input. See
  [color interpretation](user-guide.md#color-interpretation-matters).

Where each check runs is described in the
[developer guide](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/development/README.md#validation-records).
