# Processing and graphics

Version 0.2.2 includes CPU detection, Auto inference recovery and device-aware
setup. Older 0.2.1 engines require a compatible GPU for chart and plane detection.

Choose **Auto**, **CPU** or **GPU** for image correction. The default Fast mode
combines the enabled GC, WB and CC transforms in one **65³ LUT**. Spatial flat-field
correction (FFC) runs separately.

| Choice | Behavior |
| --- | --- |
| Auto | Uses CPU below 1,048,576 pixels and attempts GPU for larger images. |
| CPU | Applies correction on the processor. Useful without a compatible correction GPU. |
| GPU | Attempts graphics acceleration, with CPU recovery if application fails. |

Advanced **Detection engine** settings control chart and plane inference separately.
Auto tries eligible GPU engines, then CPU inference when GPU initialization or
execution fails. Windows prefers DirectML; Linux uses WebGPU/Vulkan and Apple
silicon uses WebGPU/Metal. Explicit CPU detection bypasses GPU initialization.
Forced GPU engines return an actionable error instead of changing engines.

The existing device-details panel reports the backend actually used, including
CPU recovery. A cached detection retains the backend that originally produced it.

## Choosing a mode

Leave **Auto** selected for everyday use. GPU work has initialization and data
transfer costs, so a small image can finish sooner on CPU. Loading, fitting, FFC
and image encoding also contribute to the total processing time. A faster GPU
pass may have only a small effect when those stages dominate the total time.

In **Advanced**, use the previous processing method to reproduce older results.
It evaluates the stages separately and is not a higher-accuracy setting. Keep the default
unless you need to match an older workflow. Inputs outside the combined LUT's
domain use the established safe path automatically.

Reports include the backend actually used and any CPU recovery. Device identity
is included where the runtime can establish it. Diagnostic details
are available when troubleshooting; the main controls use the simpler mode names.

## Drivers and platforms

Keep graphics drivers current through your operating system or GPU vendor. The
application includes its private inference runtime, not a replacement system
graphics driver. CPU inference can detect charts and white planes and fit a new calibration
without GPU support. CPU correction can also apply existing saved models.

WSL GPU use is experimental and requires the separately built Mesa Dozen add-on
in the current setup. Official Linux downloads do not include that add-on.
See [WSL setup](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/installer/README.md#experimental-wsl-gpu-setup). Native Ubuntu
uses its OS/vendor driver. ARM64 graphics and Apple-silicon desktop workflows
remain experimental. Apple-silicon macOS has an [application bundle](../macos-install.md)
as well as source-build instructions.

For numerical comparisons and hardware-specific results, see the
[developer measurements](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/development/README.md#historical-compute-measurements).
See [compatibility](../compatibility.md) for platform requirements and limitations.
