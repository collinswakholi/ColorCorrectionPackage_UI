# ColorCorrect Studio

ColorCorrect Studio calibrates still-image color from photographed charts. It combines an Avalonia desktop interface, a Rust engine and a dependency-free Python SDK.

1. Add a chart photograph and check its detected patch overlay.
2. Use the built-in chart values or enter measured reference values.
3. Fit the correction, inspect the before/after results, and apply the saved model to other photographs.
4. Export corrected images, reports, a reusable model or a cube LUT.

For uneven lighting, supply a matching blank-field photograph and enable **Flat-field correction (FFC)**. A cube LUT cannot include this spatial correction. The app processes still images; video processing has been removed.

## Start here

- [User guide](docs/user-guide.md): calibration, custom references, FFC, batches and exports.
- [Python API](api/README.md): familiar `run` / `predict_images` workflow; [typed API](docs/api/README.md) for explicit chart assignments.
- [Build and install from source](docs/building.md), including Apple-silicon macOS.
- [Windows/Linux packaging](installer/README.md) and [validation status](docs/native-validation.md).
- [Color-science checks](docs/color-science-validation.md) and [model provenance](docs/model-provenance.md).

Press **F1** in the app for the searchable offline guide. Python is optional for the desktop app.

## Processing

**Auto (recommended)**, **CPU**, and **GPU** choose the device for image correction. All use the combined 65³ correction LUT by default. Auto uses CPU for small images and GPU for larger ones; GPU correction can recover on CPU. Chart and flat-field plane detection require a usable GPU inference backend.

Windows Auto selects DirectML. Other supported routes use WebGPU/Vulkan; WSL requires its host-provided GPU translation stack. GPU availability depends on the actual driver and adapter. No Torch, LibTorch or CUDA add-on is required. [Compute behavior and measured limits](docs/compute/README.md)

Input/output settings describe pixel encodings. With sRGB/D65 input, embedded ICC profiles are converted to working sRGB and EXIF orientation is honored. Explicit non-sRGB inputs use their stored pixel encoding. FFC, gamma correction and white balance require encoded sRGB/D65. Exported non-sRGB pixels need matching interpretation downstream.

## Python example

Install the SDK with `python -m pip install ./api` and keep a built or extracted native application available:

```python
from colorcorrect import ColorCorrection, Config

with ColorCorrection(Config(save_path="results"), root="/path/to/ColorCorrectStudio") as cc:
    calibration = cc.run(Image="chart.jpg")  # Optional White_Image="blank.jpg"
    result = cc.predict_images(images=["scene1.jpg", "scene2.jpg"])
    print(calibration.metrics)
    print(result.outputs)
```

The wheel contains the SDK; it does not contain native models, graphics drivers or the worker.

## Development and evidence

The current local native candidate is **0.2.0**, following native app 0.1.0. The
Python SDK keeps its separate **2.2.1** version. Protocol and saved-data schema
versions are independent of either release number. These candidates are not yet
published releases.

The pinned toolchains are .NET 10.0.402 and Rust 1.95.0. See [building](docs/building.md) for commands and [contributing](CONTRIBUTING.md) for source checks. Settings and sessions retain the legacy `CCP-New` data location for upgrade compatibility.

Windows host component tests and extracted app/API runs in Windows Sandbox and WSL have recorded evidence. These do not certify every physical GPU or architecture. Windows/Linux x64 and ARM64 are build targets; Apple-silicon macOS is source-only at this stage. [Native acceptance requirements](docs/native-validation.md)

Generated builds, research experiments, private photos/sessions and raw diagnostics stay locally ignored. Essential regression tests and reviewed fixtures remain in source. No commit, push, workflow execution or publication is implicit in a local build.
