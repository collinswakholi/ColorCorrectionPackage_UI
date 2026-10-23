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
this path contract does not replace native macOS validation.

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

## Typed options and outcomes

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

## Validation and compatibility

```console
python -m unittest discover -s api/tests -v
python api/tests/integration_sdk.py --root .
python api/tests/integration_reference_layouts.py --samples path/to/private-fixtures
python api/tests/integration_workflow.py --root .
```

The first command uses isolated native-process fixtures to test API behavior. The
photograph integration scripts use the five public files in `test_images/` by
default; `--samples` overrides that directory. They require the real worker and
create fresh artifacts; missing required fixtures/capabilities fail. The SDK
integration imports the same runnable examples used by this documentation and
checks built-in/custom references, batch results, reload, LUT export, and FFC reuse.
The optional layout integration requires three privately retained generated
rasters: `synth_cast.png`, `synth_spyder.png`, and `synth_sg.png`. Pass their directory
explicitly. These fixtures are not distributed. Missing files write a `skipped`
verification result and exit 2; they do not count as passed coverage. A complete
run tests four reference layouts, including Passport compatibility, in three
import orders using known geometry. It makes no photographed-detector accuracy
claim. Installed app/API acceptance runs belong in Sandbox and WSL.

Fitted chart errors and CC-only cross-validation are not independent proof of
physical correction accuracy. The saved report describes the validation scope.
Input/output color spaces remain independent from reference-value encoding.
Non-sRGB/D65 input requires FFC, GC, and WB disabled.

Legacy imports (`Engine`, `Calibration`, `EngineError`), scripts, direct
`python api/colorcorrect.py`, `python -m colorcorrect`, and the `colorcorrect`
console command remain available. The existing low-level API's output defaults
are unchanged. [Advanced Engine API and NDJSON protocol](protocol.md) document the
full transport and legacy behavior.
