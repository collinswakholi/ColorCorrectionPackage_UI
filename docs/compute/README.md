# Processing and graphics

Choose **Auto**, **CPU** or **GPU** for image correction. The default Fast mode
combines the enabled GC, WB and CC transforms in one **65³ LUT**. Spatial flat-field
correction (FFC) runs separately.

| Choice | Behavior |
| --- | --- |
| Auto | Uses CPU below 1,048,576 pixels and attempts GPU for larger images. |
| CPU | Applies correction on the processor. Useful without a compatible correction GPU. |
| GPU | Attempts graphics acceleration, with CPU recovery if application fails. |

These choices affect correction, not chart or plane detection. Detection requires
GPU inference. Windows **Automatic** detection prefers DirectML; other available
routes use WebGPU/Vulkan. Missing libraries or an unavailable adapter produce an
actionable error rather than silently selecting CPU inference.

## Choosing a mode

Leave **Auto** selected for everyday use. GPU work has initialization and data
transfer costs, so a small image can finish sooner on CPU. Loading, fitting, FFC
and image encoding also contribute to the total processing time. A faster GPU
pass may have only a small effect when those stages dominate the total time.

In **Advanced**, use the previous processing method to reproduce older results.
It evaluates the stages separately and is not a higher-accuracy setting. Keep the default
unless you need to match an older workflow. Inputs outside the combined LUT's
domain use the established safe path automatically.

Reports include the device actually used and any CPU recovery. Diagnostic details
are available when troubleshooting; the main controls use the simpler mode names.

## Drivers and platforms

Keep graphics drivers current through your operating system or GPU vendor. The
application includes its private inference runtime, not a replacement system
graphics driver. CPU correction can apply a saved model when GPU inference is
unavailable, but it cannot detect a chart to fit a new one.

WSL GPU use is experimental and requires the separately built Mesa Dozen add-on
in the current setup. Official Linux downloads do not include that add-on.
See [WSL setup](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/installer/README.md#experimental-wsl-gpu-setup). Native Ubuntu
uses its OS/vendor driver. ARM64 graphics and Apple-silicon desktop workflows
remain experimental; macOS is source only.

For numerical comparisons and hardware-specific results, see the
[developer measurements](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/development/README.md#historical-compute-measurements).
See [compatibility](../compatibility.md) for platform requirements and limitations.
