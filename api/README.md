# ColorCorrect Studio Python API

Install locally with `python -m pip install ./api`. The SDK has no Python runtime
dependencies. An extracted ColorCorrect Studio native package or built source
checkout is required separately; the wheel does not contain native runtimes/models.

For a workflow resembling `ColorCorrectionPipeline`:

```python
from colorcorrect import ColorCorrection, Config

with ColorCorrection(Config(save_path="results"), root="/path/to/ColorCorrectStudio") as cc:
    calibration = cc.run(Image="chart.jpg")  # Optional White_Image="blank.jpg"
    result = cc.predict_images(images=["image1.jpg", "image2.jpg"])
    print(calibration.metrics)
    print(result.outputs)
    calibration.export_lut("correction.cube")
```

This preserves the familiar fit/predict workflow, with explicit differences:
inputs are file paths (including `pathlib.Path`), outputs are typed results with
artifact paths, and failures raise actionable exceptions. It does not accept
NumPy arrays, return the old `(metrics, images, errors)` tuple, load pickle models,
or silently translate the original package's algorithm dictionaries. Use
`Config(options=CalibrationOptions(...))` for documented advanced settings.
FFC is inferred from `White_Image` by default; `do_ffc=True` requires that image.
Each operation creates a unique directory under `save_path`.

Reload a Studio model in a new process:

```python
with ColorCorrection(Config(save_path="corrected"), root="/path/to/ColorCorrectStudio") as cc:
    cc.load_model("results/calibration-123/model.json")
    result = cc.predict_image("scene.jpg")
```

The lower-level typed API remains available:

```python
from colorcorrect import Client

with Client(root="/path/to/ColorCorrectStudio") as client:
    calibration = client.calibrate("chart.jpg", output_dir="results")
    result = calibration.apply("scene.jpg", output_dir="corrected")
    print(result.outputs)
    print(calibration.path)  # Load this saved JSON from a later Client.
```

See `docs/api/README.md` in the repository for custom references, batch
results, model reuse, typed options, and the advanced NDJSON protocol.

Legacy `Engine` imports, raw replies, scripts, `python -m colorcorrect`, and
`python api/colorcorrect.py` remain supported for still images. Video methods and
processing have been removed. Automatic GPU inference and wgpu LUT processing are the default.

Inference accepts `auto`, `directml`, `webgpu`, and `vulkan` (native WebGPU over Vulkan).
It never falls back to a CPU execution provider. Missing adapters, libraries, or
unsupported operators produce a processing error. `backend="cpu"` selects CPU
LUT application while retaining automatic GPU inference. Calibration/regression
fitting and image I/O remain on CPU. Inspect `result.backends.actual` and
`result.backends.diagnostics` for actual execution and LUT recovery details.

Calibration and application use the combined 65³ GC/WB/CC LUT by default:

```python
from colorcorrect import ApplyOptions

result = calibration.apply(
    ["scene1.jpg", "scene2.jpg"], output_dir="fast-results",
    options=ApplyOptions(),  # fast_mode=True by default
)
```

Fast mode is enabled by default for calibration and application.
`fast_mode=False` selects the standard separate stages and 33³ CC LUT.
Fast mode approximates the fitted model; it does not change its coefficients.
Flat-field correction remains separate. CPU LUT processing and GPU recovery use
the same 65³ table. Inspect `result.backends.diagnostics` for the actual method.
`CalibrationOptions(fast_mode=True)` also renders calibration output this way;
fitting and intermediate previews retain their stage calculations.
Both options require the worker's `combined_lut65_v1` capability.
The experimental `cc_method="direct"` option has been removed.
