# Processing and graphics

Chart and white-plane detection run on a GPU or the CPU, and image correction has
its own device choice.

## Image correction

The default **Fast** mode combines the enabled GC, WB and CC transforms in one
**65³ LUT**. When FFC is enabled, it runs in the same pass as that LUT, on the
GPU or the CPU. Choose the device under **Processing settings**:

| Choice | Behavior |
| --- | --- |
| Auto | Uses the CPU for images under about 1 MP (1,048,576 pixels). For larger images, the engine times the GPU against the CPU once, on its first large image, and uses whichever is faster. |
| CPU | Runs correction on the processor. Useful without a compatible GPU. |
| GPU | Uses the GPU, finishing on the CPU if the GPU pass fails. |

Leave **Auto** selected for everyday use. Because Auto measures, an integrated
GPU that is slower than the CPU is not chosen. One GPU path serves NVIDIA, AMD,
Intel and Apple GPUs through DirectX 12, Vulkan and Metal. Loading, fitting and
image encoding also count toward the total time, so a faster correction pass can
have a small effect when those dominate. JPEG export uses SIMD encoding where the
CPU supports it.

Under **Advanced**, the previous processing method evaluates the stages
separately to reproduce older results. It is not more accurate; keep the default
unless you need to match an older workflow. Inputs outside the combined LUT's
domain use the separate-stage path automatically.

## Detection

The advanced **Detection engine** setting controls chart and plane inference
separately. Auto tries eligible GPU engines, then CPU inference when GPU
initialization or execution fails. Windows prefers DirectML; Linux uses
WebGPU/Vulkan and Apple silicon WebGPU/Metal. CPU detection skips GPU
initialization entirely. A forced GPU engine returns an error instead of
switching engines.

## Reports

The device-details panel and saved reports name the backend that actually ran,
including any CPU fallback, and the device where the runtime can identify it. A
cached detection keeps the backend that produced it. SDK and protocol replies
report correction timings and the reason for the device choice in `execution`.

## Drivers and platforms

Keep graphics drivers current through your operating system or GPU vendor. The
app includes its private inference runtime, not a system graphics driver. CPU
inference can detect charts and white planes and fit a new calibration without
GPU support, and CPU correction can apply saved models.

WSL GPU use is experimental and needs the separately built Mesa Dozen add-on,
which official Linux downloads do not include; see the
[WSL setup](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/installer/README.md#experimental-wsl-gpu-setup).
Native Linux uses the distribution or vendor driver. ARM64 graphics and
Apple-silicon desktop workflows are experimental; the Mac app is described in the
[Mac installation guide](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/macos-install.md).

Measured timings are in the
[developer guide](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/development/README.md#compute-measurements).
See [compatibility](../compatibility.md) for platform requirements and limits.
