# ColorCorrect Studio

Calibrate a camera's still images from a photographed color chart, inspect each
correction stage, and reuse the calibration across a capture session.
The native Avalonia desktop app and local Rust worker use the same pipeline.
The bundled CPU workflow works offline—no browser server or Python ML setup.

![Native workbench with the public example images](docs/images/session-workbench.png)

This screenshot is a native layout render using public samples, not a claim that
the pictured session has completed calibration.

## Get the Windows preview

Use the [v0.1.0 Windows preview release page](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/tag/v0.1.0)
for the unsigned **Windows 11 x64** installer, release notes, and provenance.
If that page is unavailable, publication is still pending; local QA installers
are not the release. `/releases/latest` can point to the older web application,
not this native preview. The source migration is recorded in
[PR #1](https://github.com/collinswakholi/ColorCorrectionPackage_UI/pull/1).

From the preview release page:

1. Download `ColorCorrect-Studio-win-x64.exe`, its `.sha256` file, and
   `build-info.json` from the same release.
2. Compare the checksum before running Setup:

   ```powershell
   Get-FileHash .\ColorCorrect-Studio-win-x64.exe -Algorithm SHA256
   ```

3. Check that the source commit in `build-info.json` matches the release's
   `v0.1.0` tag. The release installer is built from that reviewed, merged commit;
   development builds can have different hashes even when named identically.
4. Install and open **ColorCorrect Studio**. The GUI does not require Python or
   a separately installed .NET runtime. An unsigned installer may trigger
   SmartScreen; verify its origin and hash before deciding whether to continue.

Setup includes app-local CPU libraries, detector weights, five public samples,
illustrated offline help, the Python SDK, and notebooks. Optional CUDA is a
separate large download. It does not replace existing user-installed runtimes.
The native installer has a distinct identity and does not uninstall the legacy
**ColorCorrector** or migrate its data. Keep saved models, sessions, and outputs
outside the application folder; uninstall removes installed application files
while retaining the native user-data directory (`CCP-New`).

## Your first correction

![Separate blank-field FFC, chart-neutral GC/WB, chart-color CC, validation and reuse](docs/images/workflow.svg)

1. Add `test_images/ColorCard_Big.JPG`, press **Detect**, and inspect the patch
   overlay and selected chart face.
2. If using FFC, load **`test_images/Blank.JPG` in the FFC section**. It is the
   separate blank-field reference—not a color chart. A missing or unusable blank
   fails an enabled FFC operation instead of silently skipping it.
3. Enable the required stages and choose **Fit + apply**:
   **FFC → GC → WB → CC**. GC/WB learn from the detected chart's named neutral
   patches; CC learns from its color patches. GC has no degree setting.
4. Inspect stage previews, per-patch ΔE00, and held-out validation. Save the
   reusable model as JSON; a low training error alone is not enough.
5. Switch **Model** to **Use loaded**, select another image from matching capture
   conditions, and choose **Apply model**. The saved GC/WB/CC transformations are
   reused; a chart is not required on each image.
6. Press **Ctrl+S**, choose outputs, and **Save selected**. Preview completion
   alone does not save full-resolution files. Keep originals and outputs separate.

Use the large left/right arrows or **Alt+Left / Alt+Right** to browse session
images. The tabs above the preview switch correction stages, not images.
Press **F1** or **?** for searchable illustrated help without internet access.

Choose among **CCM, root-polynomial 2/3, polynomial, PLS, and NN**. The polynomial
method uses the protocol name `linear`. NN supports editable widths such as
`100` or `[64, 32]`, Adam/SGD/RMSProp, batch normalization, and dropout. Cube LUT
export is available for the global color transform; it cannot include spatial FFC.
**This app processes still images only. Video and FFmpeg/ffprobe are not included.**

## Automate without the GUI

Use the local process API from Python or any language that exchanges JSON lines.
It does not start an HTTP service or need an API key. SDK 1.1 preserves the
low-level options and adds a short calibration workflow.

From the installed app folder or a built checkout, with Python 3.10+:

```powershell
python api/colorcorrect.py devices
```

See the [API quick start and full option reference](docs/api/README.md),
[end-to-end notebook](notebooks/01_headless_workflow.ipynb), and
[six-method / NN-options notebook](notebooks/02_compare_fit_methods.ipynb).
Copy notebooks to a writable folder and set `COLORCORRECT_ROOT` to the installed
app folder; the notebook guide lists its optional dependencies. The SDK itself
uses only Python's standard library.

## Know the boundaries

- **Capture conditions:** use an appropriate physical chart and fixed exposure,
  lighting, geometry, and camera settings. A detected layout cannot certify that
  a chart's colors match the bundled reference. FFC needs a matching blank field.
- **Color encodings:** sRGB/D65 input honors embedded ICC profiles by normalizing
  to working sRGB; untagged input is assumed sRGB. Other explicit input encodings
  bypass ICC conversion and must match the stored pixels. FFC/GC/WB require
  encoded sRGB/D65. Non-sRGB exports do not automatically receive a matching ICC
  profile, and the preview is not a calibrated proofing viewer.
- **Validation:** held-out chart scores assess CC conditional on fitted GC/WB,
  not the entire imaging pipeline independently. Use a separately photographed
  chart to validate a capture setup. Apply-only images without a chart have no
  measured chart ΔE00.
- **Acceleration:** CUDA can accelerate chart inference and LUT image application;
  DirectML can accelerate plane detection and LUT application where available.
  NN training, regression fits, and many preprocessing stages remain on CPU.
  Check request telemetry—selecting a GPU policy is not proof of GPU execution.

Read the [illustrated user guide](docs/user-guide.md) and
[color-science validation record](docs/color-science-validation.md) before using
the results for quantitative work.

## Build, contribute, and migrate

Native source lives at the repository root: `app/`, `engine/`, `api/`, `models/`,
`refs/`, `notebooks/`, and `test_images/`. No sample junctions or sibling projects
are required. Toolchains are pinned to **.NET SDK 10.0.402** and **Rust 1.95.0**;
build-time LibTorch SDKs are separate from packaged runtime libraries.

| Platform | Native preview availability |
|---|---|
| Windows 11 x64 | Unsigned v0.1.0 preview; use its explicit release page above |
| Linux x64 | Source and packaging scripts retained; native-host validation pending |
| macOS arm64 | Source and packaging scripts retained; native-host validation pending |

Start with [building and testing](docs/building.md),
[CONTRIBUTING](CONTRIBUTING.md), [release gates](docs/releasing.md),
[the live/clean-VM validation record](docs/validation-still-preview.md),
[SECURITY](SECURITY.md), and [CHANGELOG](CHANGELOG.md).

The old browser/Flask implementation is preserved on
[legacy-web](https://github.com/collinswakholi/ColorCorrectionPackage_UI/tree/legacy-web)
and [legacy-web-final](https://github.com/collinswakholi/ColorCorrectionPackage_UI/tree/legacy-web-final).
[Legacy downloads](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/tag/v2026.05.08-build.32)
and existing Docker images remain unchanged. REST clients need to migrate to the
local process API. **Old `.pkl` models require refitting to native JSON**; there is
no pickle converter or Flask compatibility service. See [migration](docs/migration.md).

## Research and licenses

Research background: Wakholi et al. (2026), *A systematic color correction pipeline
for controlled-environment imaging*, The Plant Phenome Journal 9(1), e70067,
[DOI: 10.1002/ppj2.70067](https://doi.org/10.1002/ppj2.70067).
The illustrated help is bundled offline; the study PDF is **not bundled**.
Citation metadata is in [CITATION.cff](CITATION.cff).

Application source retains its [MIT license and attribution](LICENSE).
Dependencies and detector weights have separate terms, including the supplied
Ultralytics detector's AGPL notice. Review [third-party notices](THIRD_PARTY_NOTICES.md)
and [model provenance and source access](docs/model-provenance.md); the MIT license
does not relicense bundled weights or dependencies.
