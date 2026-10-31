# ColorCorrect Studio Python API

Fit a correction from a chart photograph, then apply it to still images captured
under the same conditions. The SDK requires Python 3.10+ and has no Python ML or
GUI runtime dependencies. Install the wheel from your application's `python/`
folder, or install from this checkout:

```sh
python -m pip install ./api
```

The wheel does **not** contain the native worker, models, or inference libraries.
Keep the installed/extracted application intact and set `root` to that folder.
The native application and SDK are versioned independently; this SDK is 2.2.2.

Use the matching **0.2.2** native application for CPU inference recovery and
`backend="cpu-inference"`. Older 0.2.1 engines require GPU inference; installing
this wheel alone does not replace their native engine. For the Mac bundle, set
`root="/Applications/ColorCorrect Studio.app/Contents/MacOS"`.

## Fit, then predict

```python
from colorcorrect import ColorCorrection, Config

with ColorCorrection(Config(save_path="results"), root="/path/to/ColorCorrectStudio") as cc:
    calibration = cc.run(Image="chart.jpg")
    result = cc.predict_images(images=["scene1.jpg", "scene2.jpg"])
    print(calibration.metrics)
    print(result.outputs)
    calibration.export_lut("correction.cube")
```

Strings and `pathlib.Path` are accepted. Each calibration/application creates a
unique durable directory under `save_path`; returned paths identify the actual
artifacts. Explicit export destinations require `overwrite=True` to replace an
existing file. A `.cube` LUT contains the global transform and excludes spatial FFC.

## Edit FFC, GC, WB, and CC settings

```python
from dataclasses import replace
from colorcorrect import (
    ApplyOptions, CalibrationOptions, ColorCorrection, Config, FFCOptions,
)

config = Config(
    save_path="results",
    do_ffc=True, do_gc=True, do_wb=True, do_cc=True,
    options=CalibrationOptions(
        model="linear", degree=2,        # Polynomial CC, not gamma degree
        ffc=FFCOptions(method="pls", bins=32, smooth=5, degree=3),
        crop_rect=None,                 # Automatic white-plane detection
        cross_validate=True,
    ),
)
# These dataclasses are frozen. Make a changed configuration for another fit:
without_wb = replace(config, do_wb=False)

with ColorCorrection(config, root="/path/to/ColorCorrectStudio") as cc:
    calibration = cc.run(Image="chart.jpg", White_Image="blank.jpg")
    result = cc.predict_images(
        images=["scene1.jpg", "scene2.jpg"],
        options=ApplyOptions(output_format="jpg"),  # Inherit fitted stages/FFC
    )
    print(calibration.warnings)
    print(result.backends.actual, result.backends.diagnostics)
```

`Config.do_ffc` defaults to `None`: supplying `White_Image` enables FFC.
`do_ffc=True` requires an existing white image; `False` disables FFC even if one
is supplied. GC/WB/CC default to enabled. **The stage switches on `Config`
override their counterparts inside `Config.options`**, including
`check_saturation` overriding `CalibrationOptions.do_sat`. Set switches on
`Config` when using this workflow; use `options` for fitting details.

GC and WB are fitted automatically from the selected chart and reference values.
Their public configuration is enable/disable; there are no manual gamma-degree,
gamma-exponent, white-temperature, or channel-gain controls. Changing CC fitting
parameters requires another fit. `ApplyOptions` can disable fitted stages or
override FFC rendering settings, but cannot create a transform absent from a model.
CC degree also controls the polynomial input features of PLS/NN; use integers
1–7. The combination-term switch affects polynomial CC (`model="linear"`) only;
PLS/NN always include those terms. The detailed reference explains SDK validation versus native normalization.

See the [configuration reference](../docs/api/README.md#configuration-reference)
for defaults, bounds, units, method-specific settings, and manual white-image crops.

## Reload a saved Studio model

```python
from colorcorrect import ColorCorrection, Config

with ColorCorrection(Config(save_path="corrected"), root="/path/to/ColorCorrectStudio") as cc:
    calibration = cc.load_model("results/calibration-123/model.json")
    result = cc.predict_image("scene.jpg")
    print(result.outputs)
```

Application inherits the model's GC/WB/CC transforms and recorded FFC settings,
not the fit switches on a new `Config`. Keep the original white image accessible
for FFC reuse. If it moves, explicitly supply `ApplyOptions(white="new/path.jpg")`;
this is an intentional replacement and must still match the capture setup.
A closed client/model handle cannot restart itself: reopen and load the saved JSON.

## Custom references and explicit detection

```python
from colorcorrect import Client, ReferenceProfile

reference = ReferenceProfile.from_csv(
    "reference.csv", chart="classic", color_space="lab",
    white_point="d50", order="row-major",
)
with Client(root="/path/to/ColorCorrectStudio") as client:
    detection = client.detect("chart.jpg")
    calibration = client.calibrate(
        detection.image, detection=detection,
        reference=reference, chart_ids=[detection.charts[0].id],
        output_dir="results",
    )
    result = calibration.apply(["scene1.jpg", "scene2.jpg"], output_dir="corrected")
```

Built-in references need no `reference`. Multiple selected charts require explicit
assignments by detected ID. See the [typed API guide](../docs/api/README.md) for
mapping/import formats, partial batch failures, results, and errors.

## Processing and compatibility

Automatic inference and Fast correction are the defaults. The combined 65³
GC/WB/CC LUT approximates the fitted transform; FFC remains separate.
`fast_mode=False` uses separate stages and a 33³ CC LUT. This does not change the
fitted coefficients. Both CPU application and GPU recovery use the selected LUT.

`backend="auto"` tries an eligible GPU for chart/white-plane detection and
recovers to explicit ONNX Runtime CPU inference if initialization or execution
fails. `backend="cpu-inference"` runs detection on CPU without initializing a
GPU; it requires a worker advertising `cpu_inference_v1`. Explicit `directml`
(Windows), `webgpu`, and `vulkan` selections report an error if that inference
engine cannot execute. GPU image correction retains CPU recovery independently.

The legacy `backend="cpu"` selects CPU **image correction** and Auto detection.
Fitting, image decoding/encoding, and spatial FFC may also use CPU. Inspect
`result.backends.actual` and `result.backends.diagnostics["inference"]` when
present; a requested setting is not evidence of actual GPU execution. Setup's
selected runtime components are honored by SDK worker subprocesses.

The run/predict naming resembles
[ColorCorrectionPipeline](https://pypi.org/project/ColorCorrectionPipeline/).
Studio accepts image paths and returns typed artifact results; it does not accept
the original NumPy-array workflow, tuple results, pickle models, or algorithm
dictionaries as interchangeable inputs. The related
[study](https://doi.org/10.1002/ppj2.70067) describes the original pipeline;
its [source package](https://github.com/collinswakholi/ColorCorrectionPackage)
provides the original Python workflow. Use Studio's saved JSON models and
configuration objects when working with this API.

[Tutorial notebooks](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/notebooks/README.md) demonstrate editable stage options
and method comparisons. Legacy `Engine`, scripts, CLI entry points, and raw
replies remain available through the [advanced protocol guide](../docs/api/protocol.md).
This SDK processes still images only.

For contributors, [developer validation](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/api/VERIFICATION.md) describes
the checks and recorded compatibility evidence behind these guides.
