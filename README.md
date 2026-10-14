# ColorCorrect Studio

Native, chart-based color calibration for still images and 8-bit SDR video. An
Avalonia desktop workbench and a local Rust worker share the same correction
pipeline. No browser server, Python ML installation, or internet connection is
needed for the bundled CPU sample workflow.

![ColorCorrect Studio native workbench with public samples](docs/images/session-workbench.png)

## Windows preview

**v0.1.0 is being prepared and is not yet published.** The first native preview
targets **Windows 11 x64** and will be unsigned. When approved, download
`ColorCorrect-Studio-win-x64.exe` and its SHA-256 from the
[explicit native preview release](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/tag/v0.1.0).
Do not use `/releases/latest`: it can still identify the older stable web app.

Verify the checksum with `Get-FileHash <installer> -Algorithm SHA256`, run Setup,
and open **ColorCorrect Studio**. Unsigned installers may trigger SmartScreen;
verify the source and hash before deciding whether to continue. CPU libraries,
the .NET runtime, detector weights, five examples, offline illustrated help,
the Python SDK, and notebooks are included. Python is optional for the GUI.
CUDA is an optional large download, not part of this installer.

The native installer has its own identity and can coexist with **ColorCorrector**.
It does not uninstall the legacy app or migrate its data. Native settings use the
existing internal `CCP-New` folder; CPU libraries are app-local, not installed
over your CPU/CUDA data directories. Uninstall removes application files, not
your saved models, sessions, outputs, or user-data directory.

## From calibration to application

![FFC, chart-neutral GC/WB, chart-color CC, validation and application](docs/images/workflow.svg)

1. Add `test_images/ColorCard_Big.JPG`; detect and inspect the chart overlay.
2. For FFC, load **`test_images/Blank.JPG` as the separate blank-field reference**.
3. Enable needed FFC → GC → WB → CC stages. GC/WB use the chart's named neutrals;
   CC uses its color patches. Choose one of six fit methods and **Fit + apply**.
4. Inspect training and held-out ΔE00 and stage previews. Save model JSON.
5. Choose **Use loaded / Apply model** for images from matching capture conditions.
   **Ctrl+S / Save selected** writes full-resolution outputs; previews alone do not.

The six methods are CCM, root-polynomial orders 2/3, polynomial (`linear` protocol
name), PLS, and NN. NN offers editable layer widths, Adam/SGD/RMSProp, batch
normalization and dropout. Saved models reuse all enabled stages; a cube LUT
excludes spatial FFC. No chart on an apply-only image means no invented chart ΔE.

## Guidance and automation

Press **F1** or **?** for searchable illustrated offline help. See the
[user guide](docs/user-guide.md), [SDK 1.1 / NDJSON API](docs/api/README.md),
[runnable notebooks](notebooks/), [color-science validation](docs/color-science-validation.md),
and [migration guide](docs/migration.md).

With sRGB/D65 input, embedded ICC profiles normalize to working sRGB. Explicit
other input encodings bypass this conversion and must match stored pixels.
FFC/GC/WB require encoded sRGB/D65. Exported wide-gamut/linear bytes do not acquire
a matching ICC profile automatically; interpret their declared encoding downstream.
Held-out chart scores evaluate CC conditional on fitted GC/WB, not an independent
validation of the whole imaging pipeline. Use a separately photographed validation
chart. Video is 8-bit SDR only; HDR/high-bit-depth/wide-gamut video is rejected.

CUDA can accelerate chart inference and explicit LUT image application; DirectML
can accelerate plane detection/LUT application where available. NN training,
regression fits, and many preprocessing stages remain CPU work. A selected policy
or installed runtime is not proof that a request ran on GPU: inspect telemetry.

## Source and development

The native source is at repository root: `app/` (Avalonia and .NET integration),
`engine/` (Rust color math/detection/video/NDJSON), `api/`, `models/`, `refs/`,
`notebooks/`, and `test_images/`. No junctions or sibling projects are required.
Toolchains are pinned to **.NET SDK 10.0.402** and **Rust 1.95.0**.
See [build and test instructions](docs/building.md) and [CONTRIBUTING](CONTRIBUTING.md).

The first native binary release is Windows-only. Linux x64 and macOS arm64 source
and packaging scripts are retained; native-host build/install validation is still
required, and no new Linux/macOS binaries are promised by v0.1.0. See the
[release gates and platform status](docs/releasing.md).

## Legacy, research and license

The browser/Flask implementation remains on
[legacy-web](https://github.com/collinswakholi/ColorCorrectionPackage_UI/tree/legacy-web)
and [legacy-web-final](https://github.com/collinswakholi/ColorCorrectionPackage_UI/tree/legacy-web-final).
[Existing legacy downloads](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/tag/v2026.05.08-build.32)
and existing Docker images are unchanged. Legacy REST clients need to move to the
local process API. **Old pickle models require refitting to native JSON**; no
pickle converter or Flask compatibility service is supplied.

Research background: Wakholi et al. (2026), *A systematic color correction pipeline
for controlled-environment imaging*, The Plant Phenome Journal 9(1), e70067,
[DOI: 10.1002/ppj2.70067](https://doi.org/10.1002/ppj2.70067).
The illustrated app guide is offline; the study PDF is **not bundled**.
See [CITATION.cff](CITATION.cff).

Application source is [MIT licensed](LICENSE), preserving legacy attribution.
Bundled dependencies and weights have separate notices: see
[THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES.md) and [model provenance](docs/model-provenance.md).
