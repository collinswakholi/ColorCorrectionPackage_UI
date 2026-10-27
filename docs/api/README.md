# ColorCorrect Studio Python API

The Python API uses the same local native engine as the desktop app. Python 3.10+
is required; the SDK has no runtime dependencies on Python ML or GUI packages.
Automatic DirectML/WebGPU inference is the default, with CPU inference fallback disabled. LUT application uses wgpu with CPU recovery; `backend="cpu"` selects CPU LUTs while retaining GPU inference.

Prefer the familiar `ColorCorrection(Config(save_path=...))` workflow if you used
the original Python package: `run(Image=...)` calibrates, and `predict_image()` /
`predict_images()` reuse the fit. See [the quickstart and migration differences](../../api/README.md).
The typed `Client` API below remains available for explicit detection and multi-chart assignments.

## Install and locate the engine

From this source checkout:

```console
python -m pip install ./api
```

To build an offline-distributable wheel with an installed build toolchain:

```console
python -m pip wheel --no-deps --no-build-isolation ./api --wheel-dir wheels
```

The wheel contains the Python SDK, not the native worker/models/runtimes. Keep an
extracted desktop package or built checkout intact. `Client()` discovers resources
from the source checkout or current directory. An installed SDK can use
`Client(root="/path/to/app")` from any working directory. Explicit `executable=`
selects a worker; environment alternatives are `CCP_NEW_UI_ROOT` and
`CCP_ENGINE_EXE` (or the legacy `COLORCORRECT_EXE`). No server is needed.
An explicit macOS `.app` bundle root also resolves its `Contents/MacOS` resources;
on Apple silicon, build the app from source using the
[macOS instructions](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/building.md). Studio does not provide macOS application binaries.

## Calibrate and apply

```python
from colorcorrect import Client

with Client(root="/path/to/ColorCorrectStudio") as client:
    calibration = client.calibrate("chart.jpg", output_dir="results")
    result = calibration.apply("scene.jpg", output_dir="corrected")
    print(result.outputs)
    print(calibration.metrics)
    print(calibration.path)
```

Each operation creates a unique durable subdirectory inside the required
`output_dir`. Inputs accept strings or `pathlib.Path`; relative paths use the
caller's working directory. The original image is never used as a destination.
`calibration.path` identifies the saved JSON model. Keep the client open while
using its model handles.

The first detected chart is selected by default, preserving the original fit
behavior. Use `detect()` and explicit `chart_ids` to select another chart or
combine charts. Do not reuse geometry after modifying or replacing the image.

## Detection and client settings

`Client(root=..., backend="auto", timeout=600, progress=None)` owns one serialized
worker. `timeout` is a positive finite number of seconds per command (startup is
bounded separately); `progress` receives text on the calling thread and must not
issue recursive requests. `executable=` explicitly selects a worker; keep its
compatible models/references/runtime together. `backend` accepts `auto`, `cpu`,
`directml`, `webgpu`, or `vulkan`. CPU selects correction only; detection remains
GPU-only. DirectML is a Windows route; actual availability is checked at execution.

`DetectionOptions(confidence=None, crop=0.75, prefer=None, allow_recall=True)`
configures automatic detection. `confidence=None` retains the native detector
threshold; an explicit confidence is a finite value 0–1. `crop` is the patch
interior fraction (0.01–1), `prefer` is `classic`, `passport`, `spyder`, or
`digital-sg`, and `allow_recall` is a boolean. A preference does not add a newly
trained detector or guarantee that chart type is present. `Client.calibrate`
accepts `detection_options=` when it must detect, or `detection=` to reuse an
unchanged image's snapshot.

## Configuration reference

Use `Config` for the familiar run/predict workflow or pass `CalibrationOptions`
directly to `Client.calibrate`. The objects are frozen dataclasses: use
`dataclasses.replace` to create a changed configuration. **`Config.do_ffc`,
`do_gc`, `do_wb`, `do_cc`, and `check_saturation` override the corresponding
nested `CalibrationOptions` switches.** A new `Config` does not change an already
loaded model's transforms; apply-time overrides belong in `ApplyOptions`.

```python
from dataclasses import replace
from colorcorrect import (
    ApplyOptions, CalibrationOptions, ColorCorrection, Config, FFCOptions,
    NeuralNetworkOptions,
)

config = Config(
    save_path="results", do_ffc=True, do_gc=True, do_wb=True, do_cc=True,
    options=CalibrationOptions(
        model="rpcc2", cross_validate=True,
        ffc=FFCOptions(method="poly", bins=32, smooth=5, degree=3),
        crop_rect=None,
    ),
)
no_gamma = replace(config, do_gc=False)  # Use this in a new run to refit without GC.
polynomial = replace(config, options=replace(config.options, model="linear", degree=3))
pls = replace(config, options=replace(config.options, model="pls", pls_components=3))
neural = replace(config, options=replace(
    config.options, model="nn",
    nn=NeuralNetworkOptions(hidden_layers=(64, 32), optimizer="adam", epochs=200, patience=50),
))

with ColorCorrection(config, root="/path/to/ColorCorrectStudio") as cc:
    calibration = cc.run(Image="chart.jpg", White_Image="blank.jpg")
    result = cc.predict_image("scene.jpg", options=ApplyOptions(output_format="png"))
    # Controlled comparison: omit a fitted stage while applying the same model.
    without_wb = cc.predict_image(
        "scene.jpg", options=ApplyOptions(do_wb=False), output_dir="without-wb",
    )
    print(calibration.warnings, result.warnings)
    print(result.backends.actual, result.backends.diagnostics)
```

GC fits a monotonic tone-response transform and WB fits chart-neutral channel
scales from the selected reference context. Their exposed controls are `do_gc`
and `do_wb`; manual gamma exponents/degrees, temperature, and channel gains are
not API options. Toggling a stage for application is an ablation, not a refit.
Use a new calibration when changing the intended fitting pipeline.

### Stage and rendering switches

| Field | Default | Meaning |
| --- | --- | --- |
| `Config.save_path` | Required | Parent for unique durable run directories |
| `do_ffc` | `None` | At calibration, enable FFC when a white image is supplied; `True` requires one; `False` disables it |
| `do_gc`, `do_wb`, `do_cc` | `True` at calibration | Fit/apply the respective stage |
| `Config.check_saturation` / `CalibrationOptions.do_sat` | `True` | Enable chart saturation handling |
| `skip_saturated` | `False` | Exclude saturated chart observations when fitting |
| `fast_mode` | `True` | Render combined GC/WB/CC through a 65³ LUT; spatial FFC remains separate |
| `write_output` | `True` | Write a full-resolution corrected image; `False` retains model/report, without an output-image path |
| `cross_validate` | `True` | Request **CC-only** held-out chart validation; earlier stages remain fitted from the full chart |
| `phase2_teacher` | `False` | Request advanced teacher diagnostics; not needed for ordinary correction |

`ApplyOptions.do_ffc/do_gc/do_wb/do_cc` default to `None`, which preserves the
recorded model behavior. Explicit `False` omits a stage. Explicit `True` for
GC/WB/CC requires a corresponding fitted transform. FFC additionally requires a
matching accessible white image. An absent transform cannot be recovered by
turning a switch on.

### FFC: white-image surface fitting

FFC requires a separate blank-field image captured with matching illumination,
framing, and geometry. It is spatial, so a global cube LUT cannot represent it.
`crop_rect=None` uses automatic plane detection. A manual rectangle is a string
`"x1,y1,x2,y2"` in **white-image pixel coordinates**, with nonnegative origins and
positive extent; keep it within the image. It is distinct from patch `crop`.

```python
from colorcorrect import CalibrationOptions, FFCOptions

ffc_options = CalibrationOptions(
    do_ffc=True, ffc=FFCOptions(method="pls", bins=32, smooth=5, degree=3),
    crop_rect="100,100,1900,1300",  # Example only: choose coordinates for your white image.
)
```

The table lists SDK validation bounds, not performance recommendations. Large
sample grids or degrees can consume substantial time/memory. Start with defaults.

| `FFCOptions` field | Default | Accepted values / applicability |
| --- | --- | --- |
| `method` | `"poly"` | `"linear"`, `"poly"`, `"pls"`, `"nn"` |
| `bins` | `50` | Integer 2–100000; samples per side of the spatial grid (`bins²` samples) |
| `smooth` | `5` | Odd integer 1–100001; local averaging window in pixels |
| `degree` | `5` | Integer 1–32; spatial feature degree for poly/PLS/NN; linear always uses degree 1 |
| `nn_max_iter` | `10000` | Integer 1–10000000; FFC neural optimizer iteration budget, only for `method="nn"` |
| `tol` | `1e-8` | Finite, at least `1e-15`; convergence tolerance for FFC PLS/NN |
| `cut_off` | `1.3` | Positive gain threshold for soft limiting; native effective range 0.5–4 |
| `max_val` | `1.8` | Gain ceiling, greater than `cut_off`; native effective range `cut_off + 0.01` through 16 |

The SDK accepts finite positive gain-limit values (at least `1e-15`) with
`max_val > cut_off`; the native worker sanitizes them to the effective ranges
above. Use values inside those ranges to avoid an unintended adjustment.
FFC's NN is a separate surface fit; `NeuralNetworkOptions` configures **CC**, not FFC.

### CC: five families, six method choices

| `CalibrationOptions.model` | Family | Additional controls |
| --- | --- | --- |
| `"ccm"` | Color-correction matrix | Fixed matrix feature set |
| `"linear"` | Polynomial | `degree=2`; native effective degree 1–7; `include_combinations=True` includes interaction terms |
| `"rpcc2"`, `"rpcc3"` | Root-polynomial | Fixed second/third-order feature sets; `degree` does not change them |
| `"pls"` | Partial least squares | Polynomial input-feature `degree=2` (effective 1–7), interactions always included; `pls_components=0` selects automatically, with effective rank limited by available observations/features |
| `"nn"` | Neural network | Polynomial input-feature `degree=2` (effective 1–7), interactions always included; `nn=NeuralNetworkOptions(...)` |

For `linear`, `pls`, and `nn`, choose an integer `degree` from **1 through 7**.
The native worker clamps their polynomial-feature degree to that range. The
legacy SDK 2.2.1 validator accepts 1–12 for `linear` and forwards `degree` for
PLS/NN without that same strict degree validation; out-of-range values can
therefore be normalized by the worker rather than rejected. Do not rely on that
normalization. These documented examples use degrees inside the effective range.
The `include_combinations` switch affects only `model="linear"`; PLS/NN always
include interaction terms, so changing that flag does not remove their terms.
For PLS, `pls_components` accepts integers 0–10000 in the SDK; effective component
count is limited by data/features. None of these parameters configures GC.

`NeuralNetworkOptions` defaults are `(100,)` hidden layers, `optimizer="adam"`,
`batch_norm=False`, `dropout=0`, `epochs=10000`, `patience=1500`, `lr=0.001`, and
`tol=1e-6`. Use 1–4 layer widths, each 4–512; optimizers are `adam`, `sgd`, or
`rmsprop`; dropout is 0–0.8. Epoch/patience counts are integers 1–10000000;
learning rate and tolerance are finite and at least `1e-15`. Short tutorial
budgets demonstrate configuration rather than recommend a model or optimizer.

### Sampling and color interpretation

| `CalibrationOptions` field | Default | Accepted values / meaning |
| --- | --- | --- |
| `crop` | `0.75` | Patch-interior fraction, 0.01–1; unrelated to white-image `crop_rect` |
| `fit_samples` | `9` | Integer 1–100000; samples per patch axis (`9` gives a 9×9 grid) |
| `input_space`, `target_space` | `"srgb"` | RGB encoding names listed under custom references below |
| `input_white`, `target_white` | `"d65"` | White-point names listed below |
| `clip_lo`, `clip_hi` | `-0.2`, `1.3` | Finite normalized channel limits, with `clip_lo < clip_hi` |

Reference-value encoding is independent of input/output image encoding. For the
complete FFC/GC/WB workflow use encoded sRGB/D65 input. Explicit other input
encodings require `do_ffc=False`, `do_gc=False`, and `do_wb=False`; never relabel
pixels to bypass this requirement. See the [color guide](../user-guide.md#color-interpretation-matters).

### Apply-time overrides and model reuse

`ApplyOptions(output_format="png")` inherits saved stages and FFC settings.
`output_format` accepts `"png"` or `"jpg"`; `fast_mode=True` and
`write_output=True` are defaults. A new backend or rendering option does not
change the fitted coefficients or the saved model.

Supply `ApplyOptions(white=..., ffc=FFCOptions(...), crop_rect=...)` to deliberately
change the FFC asset/settings for application, or `ApplyOptions(do_ffc=False)` to
omit it. An omitted FFC crop inherits the saved crop. White content is checked
against the recorded hash on ordinary reuse; explicitly replacing the white
image is a new rendering choice and must be reviewed for compatibility.

Only parameters for active methods are interpreted. For example, CC neural
options do not affect applying a saved model, and FFC options are inactive when
FFC is explicitly disabled. Configuration changes do not rewrite earlier results.
Inspect per-image warnings, `backends.actual`, and `backends.diagnostics` for what
actually executed; requesting GPU or a stage does not prove its execution.

## Custom references

```python
from colorcorrect import Client, ReferenceProfile

reference = ReferenceProfile.from_csv(
    "reference.csv", chart="classic", color_space="lab",
    white_point="d50", order="row-major",
)
with Client() as client:
    calibration = client.calibrate(
        "chart.jpg", reference=reference, output_dir="results",
    )
    calibration.apply(["image1.jpg", "image2.jpg"], output_dir="corrected")
    calibration.export_lut("correction.cube")
```

CSV accepts a complete list of numeric triplets in the declared order, with an
optional header such as `L,a,b`; comma, tab and semicolon delimiters are supported.
For explicit coordinates use `cell,L,a,b` with cells such as `A1`, or
`row,column,L,a,b` with **1-based** rows/columns. Both formats work in the desktop editor.
Coordinate rows can appear in any order and are normalized to row-major. A chart
must contain exactly 24 triplets for `classic`, `passport` or `spyder`, or 140 for
`digital-sg`. Their layouts are 4x6 and 10x14 respectively. Duplicate, missing,
nonfinite and out-of-range coordinates are rejected atomically.

`ReferenceProfile.from_values(...)` accepts an ordinary sequence of triplets.
`ReferenceProfile.from_text(...)` parses pasted tables. `to_json(path)` and
`from_json(path)` preserve profile identity, revision, source encoding and values.
Creating a new profile produces a new ID; use `dataclasses.replace(profile,
revision=profile.revision + 1, values=new_values)` for an intentional new revision.

Supported reference encodings are `lab`, `xyz`, `srgb`, `linear-srgb`,
`display-p3`, `adobe-rgb-1998`, `rec2020`, `linear-rec2020`, `prophoto-rgb`, and
`acescg`. Lab uses ordinary L*a*b* with `scale=1`. XYZ accepts `scale=1` or `100`;
RGB accepts `scale=1`, `255`, or `65535`. White points are `d50`, `d55`, `d60`,
`d65`, `d75`, `a`, `c`, and `e`. Values remain unclipped. The native engine performs
the authoritative conversion to canonical Lab D50; the Python SDK does not add a
second color-science implementation.

Lab L* must be 0–100, XYZ must be nonnegative, and RGB must lie inside its
declared numeric scale. Lab chroma and canonical colorimetric results retain
colors outside the display gamut.

For multiple charts, bind each profile to the exact detected ID:

```python
with Client() as client:
    detection = client.detect("two_charts.jpg")
    first, second = detection.charts[:2]
    calibration = client.calibrate(
        detection.image, detection=detection,
        references={first.id: reference_a, second.id: reference_b},
        output_dir="results",
    )
```

The mapping selects its chart IDs automatically. Pass `chart_ids=` only to set an
explicit selection/order separately. All selected charts require an explicit profile
in custom mode. A single `reference=` requires one selected compatible chart; when
multiple charts are detected, explicitly select one with `chart_ids=[chart.id]`.
Passport profiles can be
used with Classic detections. Workers must advertise `custom_references_v1` and
`chart_identity_v1`; older workers fail clearly instead of ignoring the profiles.
Reference snapshots are embedded in the saved model by the engine. Applying that
model needs no external reference CSV/JSON.

## Reuse a saved model and flat-field correction

```python
from colorcorrect import CalibrationOptions, Client, FFCOptions

with Client() as client:
    calibration = client.calibrate(
        "chart.jpg", white="white.jpg", output_dir="results",
        options=CalibrationOptions(ffc=FFCOptions(method="pls", degree=5)),
    )
    model_path = calibration.path

with Client() as client:
    calibration = client.load_calibration(model_path)
    calibration.apply("scene.jpg", output_dir="corrected")
```

New models retain the white-image path and FFC settings in `python_api` provenance
alongside the native model. The white image must remain accessible when FFC is
reused. A requested FFC stage that was skipped is reported in warnings. Native
legacy models retain their existing behavior; historical metrics are `None` if
unavailable. If a legacy model requires FFC without a recorded white image, use
`ApplyOptions(white="white.jpg")` or explicitly `ApplyOptions(do_ffc=False)`.
Explicitly enabling GC/WB/CC when a model has no corresponding fitted transform
raises an actionable error before processing. Reported skipped stages also
appear in result warnings and execution diagnostics.

Closing a client is permanent. A retained model handle raises `ClosedClientError`
after close; load its saved JSON through a new client to continue. Raw Engine
clients retain their legacy restart behavior.

## Typed results and errors

```python
from colorcorrect import ApplyOptions, CalibrationOptions, DetectionOptions

with Client(backend="cpu", timeout=600, progress=print) as client:
    detection = client.detect("chart.jpg", options=DetectionOptions(confidence=0.4))
    calibration = client.calibrate(
        detection.image, detection=detection, output_dir="results",
        options=CalibrationOptions(model="rpcc3", cross_validate=True),
    )
    result = calibration.apply(
        "scene.jpg", output_dir="corrected",
        options=ApplyOptions(output_format="png"),
    )
    print(result.backends.requested, result.backends.actual)
```

Dataclasses expose detection, FFC, calibration, neural-network, and application
options for editor completion. `NeuralNetworkOptions` configures layers,
optimizer, normalization, dropout and fitting limits. Advanced raw operations
remain available through `client.engine.request(...)` and the original `Engine`.

`CalibrationModel.path`, `report_path`, and `corrected_image` identify saved
artifacts (optional paths are `None` when unavailable). `metrics` contains the native report; `warnings` lists skipped
stages and diagnostics. `ApplyResult.images` contains one `ImageResult` per input,
with `ok`, `output`, `error`, `warnings`, and `backends`. Single-image and batch
calls return the same `ApplyResult` shape. `outputs` contains successful written
paths; `failures` contains failed rows. `write_output=False` returns no output
paths. Actual backends are populated only from reported execution, never inferred
from the requested policy. Empty actual diagnostics mean unavailable evidence.

By default, any failed image raises `BatchProcessingError`. Its `.result`
contains both successful and failed outcomes. To handle failures yourself:

```python
result = calibration.apply(
    ["good.jpg", "missing.jpg"], output_dir="corrected", on_error="continue",
)
for row in result.failures:
    print(row.image, row.error)
```

Missing inputs are identified before native application; valid batch inputs can
still complete. Invalid options fail before detection/fitting. Other error types
are `InvalidInputError`, `MissingRuntimeError`, `UnsupportedCapabilityError`,
`ProcessingError`, `ProcessingTimeoutError`, and `ClosedClientError`. All derive
from `ColorCorrectError`; worker failures retain `raw_reply` diagnostics.

## LUT export

```python
with Client() as client:
    calibration = client.load_calibration("results/calibration-123/model.json")
    lut = calibration.export_lut("grade.cube", size=33)
    print(lut.path)
```

Existing destinations require `overwrite=True`. A LUT excludes spatial FFC;
the result warns when the model used FFC. This SDK processes still images only.

## Tutorials and compatibility

The [tutorial notebooks](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/notebooks/README.md)
walk through stage settings, custom references, model reuse, and method comparisons.
Use the wheel included with your app and set explicit application, photograph,
and output directories before starting the notebook kernel.

Fitted chart errors and CC-only cross-validation are not independent proof of
physical correction accuracy. The saved report describes the validation scope.
Input/output color spaces remain independent from reference-value encoding.
Non-sRGB/D65 input requires FFC, GC, and WB disabled.

Legacy imports (`Engine`, `Calibration`, `EngineError`), scripts, direct
`python api/colorcorrect.py`, `python -m colorcorrect`, and the `colorcorrect`
console command remain available. The existing low-level API's output defaults
are unchanged. [Advanced Engine API and NDJSON protocol](protocol.md) document the
full transport and legacy behavior.

Contributors can find repeatable checks and version-specific execution records
in the [developer validation reference](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/api/VERIFICATION.md).
