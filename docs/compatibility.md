# Compatibility

ColorCorrect Studio is available as an **unsigned release** for Windows and
Ubuntu. Keep the complete application folder together; its runtime, models and
libraries are included. Python is optional.

| Platform | Availability | Graphics and desktop status |
| --- | --- | --- |
| Windows 11 x64 | Installer and portable ZIP | Requires a compatible GPU/driver for chart and plane detection. |
| Ubuntu 24.04 x64 | Debian package and portable archive | Native desktop and GPU compatibility remain experimental; WSL experience does not establish compatibility with every Ubuntu desktop. |
| Windows 11 ARM64 | Installer and portable ZIP | Experimental; graphics and desktop workflows are not yet confirmed on representative ARM64 hardware. |
| Ubuntu 24.04 ARM64 | Debian package and portable archive | Experimental; graphics and desktop workflows are not yet confirmed on representative ARM64 hardware. |
| Apple-silicon macOS 14+ | Source build only | Experimental; no downloadable application or installer, and hardware GPU/desktop compatibility remains unconfirmed. |

Choose the architecture used by your operating system: **x64** for 64-bit
Intel/AMD, **ARM64** for 64-bit Arm. There are no 32-bit x86 or Intel macOS builds.
[Download and install](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/README.md#download-and-install)
or [build from source](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/building.md).

## Graphics requirements

Chart and plane detection require compatible GPU inference. Windows Automatic
detection prefers DirectML; other available routes use WebGPU/Vulkan. Install
graphics drivers through your OS or GPU vendor. The application installer supplies
its own libraries and never replaces system graphics drivers.

**Auto**, **CPU** and **GPU** choose how image correction runs. CPU can apply an
existing calibration without GPU inference. Detecting a chart and fitting a new
calibration still require GPU inference. See [processing options](compute/README.md).

## WSL

WSL GPU use is experimental. In the current WSL setup, chart and plane detection
require a separately built Mesa Dozen add-on and working Windows-host GPU support.
Official Linux downloads do not include the add-on; enabling a setting alone
cannot supply it. CPU correction with an existing model remains available.

Follow the [WSL setup instructions](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/installer/README.md#experimental-wsl-gpu-setup).
Use the Windows host's supported GPU driver; do not install a Linux kernel GPU
driver inside WSL.

## Other limits

- Packages are unsigned. On Windows, confirm the download source and checksum
  before allowing installation. Source-built macOS applications are not notarized.
- Some high-DPI layouts, graphics adapters and driver combinations remain
  unconfirmed. ARM64 and Mac graphics workflows remain experimental.
- The app processes still images. Models should be reused only for matching
  camera, lighting, exposure and image-encoding conditions.
- Full FFC/GC/WB processing expects encoded sRGB/D65 input. See
  [color interpretation](user-guide.md#color-interpretation-matters).

Developers can find the detailed platform history and coverage records through
the [developer guide](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/development/README.md#validation-records).
