# Compatibility

ColorCorrect Studio 0.2.2 includes Windows and Ubuntu packages and an
Apple-silicon macOS app bundle. Keep the complete application folder or bundle
together; its runtime, models and libraries are included. Python is optional.

Auto chart/plane inference tries compatible GPU execution and recovers to CPU.
Explicit CPU inference and CPU correction support the complete workflow without
a GPU. Installing a different Python wheel does not replace the native engine.

| Platform | Availability | Graphics and desktop status |
| --- | --- | --- |
| Windows 11 x64 | Installer and portable ZIP | CPU inference and correction, with optional DirectML/WebGPU acceleration. |
| Ubuntu 24.04 x64 | Debian package and portable archive | Native desktop and GPU compatibility remain experimental; WSL experience does not establish compatibility with every Ubuntu desktop. |
| Windows 11 ARM64 | Installer and portable ZIP | Experimental; graphics and desktop workflows are not yet confirmed on representative ARM64 hardware. |
| Ubuntu 24.04 ARM64 | Debian package and portable archive | Experimental; graphics and desktop workflows are not yet confirmed on representative ARM64 hardware. |
| Apple-silicon macOS 14+ | Application bundle in ZIP and tar archives | Experimental; native packaging and CPU checks are separate from hardware GPU and interactive desktop acceptance. |

Choose the architecture used by your operating system: **x64** for 64-bit
Intel/AMD, **ARM64** for 64-bit Arm. There are no 32-bit x86 or Intel macOS builds.
[Download and install](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/README.md#download-and-install)
or [build from source](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/building.md).

## Graphics requirements

Chart and plane detection use eligible GPU inference with
explicit CPU recovery under Auto. Windows prefers DirectML; other routes use WebGPU/Vulkan or Metal. Install
graphics drivers through your OS or GPU vendor. The application installer supplies
its own libraries and never replaces system graphics drivers.

**Auto**, **CPU** and **GPU** choose how image correction runs. Advanced detection
settings independently offer Auto, CPU, and platform-specific GPU engines. Auto
can create new calibrations without a GPU; a forced GPU engine reports an error
instead of changing inference engines. See [processing options](compute/README.md).

## WSL

WSL GPU use is experimental. In the current WSL setup, GPU chart and plane detection
require a separately built Mesa Dozen add-on and working Windows-host GPU support.
Official Linux downloads do not include the add-on; enabling a setting alone
cannot supply it. CPU inference and correction work without the add-on.

Follow the [WSL setup instructions](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/installer/README.md#experimental-wsl-gpu-setup).
Use the Windows host's supported GPU driver; do not install a Linux kernel GPU
driver inside WSL.

## Other limits

- Windows/Linux packages are not publisher-signed. Mac bundles use ad-hoc
  signing and are not Developer ID signed or notarized. Confirm the release
  source and checksums, then follow the [Mac installation guide](macos-install.md)
  for the first-launch security prompt.
- Some high-DPI layouts, graphics adapters and driver combinations remain
  unconfirmed. ARM64 and Mac graphics workflows remain experimental.
- The app processes still images. Models should be reused only for matching
  camera, lighting, exposure and image-encoding conditions.
- Full FFC/GC/WB processing expects encoded sRGB/D65 input. See
  [color interpretation](user-guide.md#color-interpretation-matters).

Developers can find the detailed platform history and coverage records through
the [developer guide](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/development/README.md#validation-records).
