# Advanced Engine API and NDJSON protocol

All native correction operations are already callable through the bundled `ccp-engine` worker. The public Python API and command-line wrapper now make that interface usable by default: **no GUI, HTTP server, API key, network connection, or Python ML dependencies**. Python 3.10+ is optional; any language that can launch a process and exchange JSON lines can use the worker directly. Desktop-only interactions such as catalog selection and window layout are not processing API operations.

## Legacy Engine workflow

For everyday work start with the [installed run/predict SDK](../../api/README.md)
and [typed configuration guide](README.md#configuration-reference). This reference
documents the preserved low-level `Engine` interface and transport. Its raw reply
shapes and legacy defaults differ from the typed SDK. Install the wheel or use
`python -m pip install ./api`; no `sys.path` modification is needed.

```python
from colorcorrect import Engine
```

A legacy fit/apply example:

```python
from colorcorrect import Engine

with Engine() as engine:
    calibration = engine.calibrate("ColorCard_Big.JPG", folder="results",
        white="Blank.JPG", model="nn", nn={"hidden_layers": [64, 32],
        "optimizer": "adam", "batch_norm": True, "dropout": 0.01})
    print(calibration.report["report_json"])  # Inspect held-out errors before reuse.
    calibration.apply(["Sample_3572.JPG", "Sample_3573.JPG"])
    print(calibration.model)  # Persist this path for another session.
```

`calibrate` detects automatically, then fits. It creates a unique `calibration-*`
subfolder (system temporary storage when `folder` is omitted), with model, report,
and corrected chart paths. Supply `folder` for durable results. `white` enables
FFC unless `do_ffc=False`; other engine defaults are unchanged. Its `apply`
remembers the FFC white and flag, creates a unique output folder by default, and
accepts every original apply option. Keep the Engine context open while using it.

All existing `detect`, `fit`, `apply`, preview, LUT and `request` methods
remain compatible. Every fit keyword is accepted by `calibrate`; explicit
`output`, `report`, `model_out`, or `handshake` overrides remain available.
Detection-specific settings go in `detect_options={...}`. A supplied handshake
skips detection. No chart-type or space settings are removed or inferred from filenames.

`run`, `apply` and `preview_stage` accept `fast_mode: true | false` (default false).
Require `combined_lut65_v1` before enabling it. It combines active GC/WB/CC stages
in a cached 65³ LUT, with FFC applied separately. Fitting coefficients and
intermediate previews keep their existing math. The final rendered metrics are
computed from the selected output. `apply_execution.method` identifies
`combined-lut65`, `cc-lut33`, `identity`, or a standard-domain fallback.
CPU/GPU chunk counts and recovery reasons are reported independently.
The typed SDK exposes `CalibrationOptions(fast_mode=True)` and
`ApplyOptions(fast_mode=True)`, both enabled by default. The raw protocol keeps
its legacy default of false. The experimental `cc_method` option was removed;
workers reject it explicitly.

![Calibration, validation, and application workflow](../images/workflow.svg)

In this legacy interface, the flow is `detect` → `fit` → inspect the report → `apply`.
The current typed SDK uses `Client.calibrate()` or `ColorCorrection.run()`.
The notebooks display the source, corrected result, and FFC preview so the numeric
results can be checked visually as well as through assertions.

Use an extracted portable package (keep `models/`, `refs/`, and native runtimes together) or this built source checkout. From its root:

```powershell
python api/colorcorrect.py devices
python api/colorcorrect.py detect --json '{"image":"test_images/ColorCard_Big.JPG","handshake_out":"chart.json"}'
```

For Windows shells with JSON-quoting differences, put parameters in a UTF-8 JSON file and use `--json @request.json`, or pipe JSON through `--json -`. The CLI writes progress to stderr and one result object to stdout; exit code 0 means complete success, 1 means failure (including partial batch failure). `--engine`, `--root`, `--policy` and `--timeout` work for every command. The CLI defaults to `auto`. Legacy `Engine()` retains its `cpu` policy default, which selects CPU LUT processing while detection still requires GPU inference.

Python import after installing the SDK:

```python
from pathlib import Path
from colorcorrect import Engine

root = Path.cwd().resolve()
samples = root / "test_images"
out = root / "api-output"
out.mkdir(exist_ok=True)

with Engine(root=root, policy="cpu", progress=print) as engine:
    detected = engine.detect(samples / "ColorCard_Big.JPG", handshake_out=out / "chart.json")
    fitted = engine.fit(
        samples / "ColorCard_Big.JPG", handshake=detected["handshake"],
        output=out / "chart_corrected.png", report=out / "report.json",
        model_out=out / "model.json", model="rpcc2",
        white=samples / "Blank.JPG", do_ffc=True,
        do_gc=True, do_wb=True, do_cc=True, ffc={"method": "pls", "degree": 5},
        cross_validate=True, phase2_teacher=False,
    )
    print(fitted["report_json"]["delta_e_2000"])
    print(fitted["report_json"].get("validation_delta_e_2000"))
    assert fitted["report_json"]["do_ffc"], "FFC skipped; inspect the white image"
    applied = engine.apply(out / "model.json", [samples / "Sample_3572.JPG"],
        out / "applied", output_format="png", do_ffc=True, white=samples / "Blank.JPG")
    engine.preview(out / "model.json", samples / "ColorCard_Big.JPG", "gc", out / "preview",
        do_ffc=True, white=samples / "Blank.JPG")
    engine.export_cube(out / "model.json", out / "grade.cube", size=33)
```

`Blank.JPG` is **only** the flat-field white reference, never a chart or validation sample. Reuse calibration only with appropriate exposure, lighting, camera setup and white-reference geometry. Outputs may overwrite existing paths; use separate output directories, as the notebooks do. Models are local JSON files, not Python pickle files.

## Runnable notebooks and tests

- [01: full chart-to-image workflow](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/notebooks/01_headless_workflow.ipynb): detection, FFC, fitting, held-out metrics, batch apply, preview and LUT.
- [02: all fit families and NN options](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/notebooks/02_compare_fit_methods.ipynb): six methods, then Adam/SGD/RMSprop with editable layers, normalization and dropout.

The notebooks use the installed SDK from any working directory, with explicit app,
sample, and durable output directories. Follow the [notebook setup guide](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/notebooks/README.md)
for environment variables, QA-only dependencies, and the fresh-kernel acceptance
runner. Both tutorials display real before/after images; the first isolates FFC.
Never reuse a detection on another image.

```powershell
python -m unittest discover -s api/tests -v
python api/tests/integration_workflow.py
```

The first command runs fast isolated transport tests, not color-science tests. The integration workflow uses real source images, covers two chart detections, fitting, all stage previews, model reload, LUT export and partial-batch failure. Integration artifacts are stored under a fresh temporary directory.

## Python API contract

`Engine(executable=None, root=None, policy="cpu", timeout=600, progress=None)` discovers the worker and resources from the app folder. Explicit paths override `CCP_ENGINE_EXE` / `COLORCORRECT_EXE` and `CCP_NEW_UI_ROOT`. Native ONNX GPU libraries are resolved beside the worker. An explicit package root is authoritative: an incomplete package fails rather than borrowing a development runtime through `ORT_DYLIB_PATH`. The legacy `cpu` policy controls LUT processing only: chart and plane inference always require DirectML or WebGPU. All file arguments accept `Path` or strings; relative paths are relative to the caller's current working directory, not the worker executable.

| Method | Operation | Required arguments |
|---|---|---|
| `devices()` | List/probe CPU/GPU assignment | none |
| `warmup()` | Initialize chart detector | none |
| `detect(image, **options)` | Detect chart geometry | chart image |
| `fit(image, *, output, report, model_out, **options)` | Calibrate FFC→GC→WB→CC | image, handshake in options, artifact paths |
| `apply(model, images, output_dir, **options)` | Apply saved model to one/many images | model JSON, image path(s), destination |
| `preview(model, image, stage, preview_dir, **options)` | Compute a selected cumulative stage | model, image, stage, destination |
| `export_cube(model, path, size=33)` | Export global transform | explicit model and .cube path |
| `request(command, **parameters)` | Complete native protocol escape hatch | command + documented JSON fields |
| `close()` | Stop worker and release pipes | none |

Always use `with Engine(...) as engine:` or `try/finally: engine.close()`. One client serializes requests; multiple clients mean independent processes/caches and multiply memory usage. Progress callbacks run on the caller thread and must not call the same client recursively. `EngineError.reply` preserves the native reply; `request(..., check=False)` opts into manual inspection, including every batch row. The native `ok` on a batch means **at least one** succeeded, whereas the Python default raises for **any** failed row.

A timeout, corrupt protocol, interrupted call or failing progress callback terminates that worker before reuse. The next call starts a fresh worker; last-fit state and caches are lost. Save model paths explicitly. The timeout covers each command; initial worker startup has a separate timeout. `close()` waits for an in-flight request, then asks the worker to flush deferred preview PNGs and shut down (up to 10 seconds, bounded by the client timeout); it forces termination if shutdown fails. Configure request timeouts to bound in-flight work. Termination cannot undo already written output files. This is a trusted local file-processing API, not a sandbox or authenticated service; do not expose it directly to untrusted network requests.

## NDJSON protocol (all native operations)

Launch `ccp-engine` with the same runtime environment as the app. Its first stdout line is `{"ok":true,"event":"ready",...}`. Write one JSON object followed by a newline and flush. Consume zero or more `{"type":"progress","message":"..."}` lines, then one final reply containing `ok`. Send one request at a time: there are no request IDs or multiplexing. Drain stderr concurrently to avoid a pipe deadlock. End with `{"cmd":"shutdown"}` (alias `quit`) or close stdin. No GUI needs to be running.

### `devices` / `warmup`

Optional `policy`: `auto`, `cpu`, `directml` (alias `dml`), `webgpu`, or `vulkan`. `auto` tries DirectML then WebGPU on Windows, and WebGPU on Linux/macOS. `cpu` selects CPU correction only; chart and plane inference still require a hardware GPU. Explicit GPU policies select that inference route; Vulkan uses the WebGPU route with its Vulkan graphics backend. Inference never falls back to CPU. Unsupported policy strings normalize to `auto` for legacy compatibility; they do not enable other inference engines.

Detection options below also apply to warmup. `devices` returns the planned `assignment`, backend availability and runtime diagnostics; planning alone does not prove execution. `warmup` returns `warmed`, `device`, `title`, or an error. Inspect the completed operation's backend diagnostics for actual execution.

### `detect`

Required `image`. Optional `handshake_out` (otherwise a temporary JSON file); `conf` detector confidence (native default), `crop` patch sampling fraction (default .75), `prefer` chart preference, `allow_recall` (true), `policy`. Returns `handshake` path, `n_charts`, `chart_type`, `cached`, and detection diagnostics. The returned path is authoritative even for cache hits. The handshake holds chart faces and patch quadrilaterals (`patch_obbs` / `patch_cells`), and can be inspected or supplied as a JSON object to `run` for manually provided chart geometry. Keep its patch IDs aligned with the matching bundled chart reference.

### `run` (Python `fit`)

Required `image`, `handshake` (path or JSON object), `output`. Always provide `report` and `model_out` to avoid the native defaults `report.json`/`model.json` in cwd.

| Field | Meaning / native default |
|---|---|
| `model` | `ccm`, `rpcc2` (default), `rpcc3`, `linear`, `pls`, `nn` |
| `face`, `use_all` | Zero-based detected chart face (0); combine detected faces (false) |
| `crop`, `fit_samples` | Interior patch crop (.75); samples per axis within each patch (9, a 9×9 grid) |
| `do_ffc`, `white`, `crop_rect` | FFC (defaults on when white supplied), white image, optional integer pixel rectangle string `x1,y1,x2,y2` |
| `do_gc`, `do_wb`, `do_cc` | Enable automatic range-safe GC, WB, CC (all true); no GC degree |
| `do_sat`, `skip_saturated` | Saturated-patch extrapolation (true), skip saturated fit samples (false) |
| `degree`, `include_combinations` | Polynomial feature degree (2; native effective range 1–7) for linear/PLS/NN; cross-term toggle (true) affects linear only, while PLS/NN always include interactions |
| `pls.components` | PLS component count; 0 selects automatically |
| `nn` | See NN options below |
| `spaces.input`, `spaces.target` | Actual input/output pixel encodings (both `srgb`) |
| `whites.input`, `whites.target` | White points (both `d65`) |
| `clip_lo`, `clip_hi` | Fit/extrapolation bounds (-.2, 1.3); output image serialization clips to [0,1] |
| `cross_validate` | CC-only held-out chart validation (true); prior stages remain fitted on the full chart |
| `phase2_teacher` | Auxiliary teacher artifacts (true); set false unless needed |
| `write_output` | Write final image (true); false still writes report/model |
| `preview_dir`, `preview_mode` | Optional preview artifacts; `none`, `corrected` (default), `all`, or stage name |
| `lut_out` | Optional .cube export, excluding spatial FFC |

FFC options in `ffc`: `method` (`linear`, `poly` default, `pls`, `nn`), `bins` (50), `smooth` (5, positive odd window), `degree` (5), `nn_max_iter` (10000), `tol` (1e-8), `cut_off` (1.3), `max_val` (1.8). This is independent of CC's NN settings. Explicitly enabling FFC without a `white` path is rejected by both the Python API and native worker. An unusable supplied white image can still cause FFC fitting to be skipped; inspect `report_json.do_ffc` and warnings rather than assuming it ran.

NN options in `nn`: `hidden_layers` array (1–4 widths, each 4–512; Python also accepts a single integer), `hidden_size` fallback (100), `optimizer` (`adam`, `sgd`, `rmsprop`), `batch_norm` (false), `dropout` (0, range 0–.8), `epochs` (10000), `patience` (1500), `lr` (.001), `tol` (1e-6). Example: `{"hidden_layers":[128,64,32],"optimizer":"rmsprop","batch_norm":true,"dropout":0.1}`. The worker clamps several numeric parameters; use documented ranges rather than relying on silent normalization.

Reply: `output`, `report`, `model_out`, `lut_out`, `preview_dir`, `chart_type`, `has_delta_e`, `report_json`, backend diagnostics. Report contains fitted-chart `delta_e_2000`, per-stage metrics, timings, optional `validation_delta_e_2000` and warnings. A small training error is not a claim about arbitrary scene accuracy.

### `apply`

Required `model` (saved JSON), `images` (array), `output_dir`. Optional `output_format`: `jpg` default or `png`; `write_output`, `preview_dir`, `preview_mode`, `policy`; stage toggles `do_gc`/`do_wb`/`do_cc` default to saved model state. **`do_ffc` defaults false even if the model was fitted with FFC.** Supply `do_ffc:true`, `white`, and compatible FFC options when needed. FFC is spatial and is not stored as a portable global correction matrix.

Results are an array with `ok`, `image`, `output`, optional `error`/`ffc_skipped`. Duplicate file stems within a batch get distinct suffixes. Each image is processed sequentially to bound memory. `metrics_hidden:true` means there are no measured chart errors for these scene images.

### `preview_stage`

Required `model`, `image`, `preview_dir`; `stage`: `ffc`, `gc`, `wb`, `cc` or `corrected`. Stages are cumulative: GC includes FFC if explicitly enabled; WB includes prior stages. Apply's FFC and stage options also apply. No final full-resolution image is written. The preview directory contains native preview artifacts; PNG encodes may finish asynchronously while a `.rgb` preview is immediately available to the GUI.

### `export_cube`

Required `path`; provide explicit `model` (otherwise native worker uses its last successful fit). `size` defaults to the native LUT size, Python helper explicitly defaults to 33 and limits 2–129. The export honors explicit model paths even after another fit. A .cube only represents the global transform; it cannot encode FFC. Export files can be large at high grid sizes.

## Color-space contract and scientific caution

Supported encodings: `srgb`, `linear-srgb`, `display-p3`, `adobe-rgb-1998`, `rec2020`, `linear-rec2020`, `prophoto-rgb`, `acescg`. With **sRGB/D65 input**, tagged images are ICC-converted to working sRGB before correction; untagged images are assumed sRGB. EXIF orientation is applied before detection and fitting. Explicit non-sRGB/custom-white input selections bypass ICC conversion and describe the actual stored pixels. Select whites explicitly: standard sRGB/P3/Adobe/Rec.2020 use D65; ProPhoto normally D50; ACEScg normally D60. Output images are quantized/clipped ordinary RGB images, not automatically ICC-tagged archival wide-gamut deliverables. For standard screen viewing keep target sRGB/D65. Refit older models trained on incorrectly interpreted tagged inputs.

`input_normalization` describes input handling in reports. `validation_scope` explains that five-fold validation tests the CC mapping after GC/WB fitting; it is not an independent full-pipeline or physical-chart validation. Invalid embedded ICC profiles fail with a conversion error instead of being silently treated as sRGB.

Non-sRGB/D65 input requires FFC, GC and WB disabled; those existing pre-correction stages operate in encoded sRGB. CC-only calibration supports the selected spaces. Reference Lab/XYZ comparisons must be adapted to the same white before computing CIEDE2000. See the independent color-science review in the app documentation. Never change only a metadata label to claim a pixel conversion occurred.

## Design sources

The streaming client follows the [Python subprocess documentation](https://docs.python.org/3/library/subprocess.html): both output pipes are drained, launches do not invoke a shell, and timed-out processes are terminated before another request. Notebooks follow the [Jupyter notebook format](https://nbformat.readthedocs.io/en/latest/format_description.html). Scientific background: [the original ColorCorrect research](https://acsess.onlinelibrary.wiley.com/doi/full/10.1002/ppj2.70067).
