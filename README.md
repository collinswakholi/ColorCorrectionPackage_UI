# ColorCorrect Studio

Turn a photographed color chart into a reusable correction for your still images.
Check the detected patches, fit the correction, inspect the results, and apply the
saved model to photographs captured under the same conditions.

[Download 0.2.0 preview](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/tag/v0.2.0)
· [User guide](docs/user-guide.md) · [Python API](api/README.md)
· [Build from source](docs/building.md)

![A blank field supplies FFC; chart neutrals supply GC and WB; chart colors supply CC. Validate, save a model, then apply it to matching images.](docs/images/workflow.svg)

The desktop app runs locally and includes its .NET runtime, Rust engine, models,
and inference libraries. Python is optional. No Torch, LibTorch, CUDA add-on, or
browser server is needed.

## Download and install

**0.2.0 is a preview release.** Native builds, installation checks, component tests,
and installed Python API smoke tests pass on Windows/Linux x64 and ARM64. The
packages are unsigned; hardware GPU and complete interactive UI acceptance remain
separate checks. See the [validation record](docs/native-validation.md).

| Platform | Installer | Portable application |
| --- | --- | --- |
| Windows 11 · x64 | [Setup.exe](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/download/v0.2.0/ColorCorrect-Studio-0.2.0-win-x64-Setup.exe) | [ZIP](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/download/v0.2.0/ColorCorrect-Studio-0.2.0-win-x64.zip) |
| Windows 11 · ARM64 | [Setup.exe](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/download/v0.2.0/ColorCorrect-Studio-0.2.0-win-arm64-Setup.exe) | [ZIP](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/download/v0.2.0/ColorCorrect-Studio-0.2.0-win-arm64.zip) |
| Ubuntu 24.04 · x64 | [.deb](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/download/v0.2.0/colorcorrect-studio_0.2.0_amd64.deb) | [.tar.gz](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/download/v0.2.0/ColorCorrect-Studio-0.2.0-linux-x64.tar.gz) |
| Ubuntu 24.04 · ARM64 | [.deb](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/download/v0.2.0/colorcorrect-studio_0.2.0_arm64.deb) | [.tar.gz](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/download/v0.2.0/ColorCorrect-Studio-0.2.0-linux-arm64.tar.gz) |
| Apple-silicon macOS 14+ | [Source build/install instructions](docs/building.md#apple-silicon-macos-source-only) | Source only; no downloadable app or installer |

**x64** means 64-bit Intel/AMD; **ARM64** means 64-bit Arm. Choose your operating
system's native architecture. These packages do not target 32-bit x86.

On **Windows**, run Setup and open **ColorCorrect Studio**. For a portable install,
extract the entire ZIP and run `ColorCorrect-Studio.cmd`. Setup offers the included
Microsoft Visual C++ prerequisite when needed; it does not replace GPU drivers.

On **Ubuntu**, install the matching Debian package, then launch the app from the
application menu or with `colorcorrect-studio`:

```sh
sudo apt install ./colorcorrect-studio_0.2.0_amd64.deb  # ARM64: use the _arm64.deb file
colorcorrect-studio
```

For a Linux portable install, extract the archive, run `./check-system`, and launch
`./ColorCorrect-Studio` from its extracted folder. The included
`./install-dependencies --install` offers explicit prerequisite setup on Ubuntu.

Chart detection requires a compatible GPU and driver. CPU correction can apply a
saved model, but it does not provide CPU chart detection. Keep the app folder
intact; its libraries and models are private to that installation.

Release assets include SHA-256 checksums and a manifest for each architecture.
See [installation details](installer/README.md) for prerequisites, verification,
portable use, and the optional experimental WSL GPU setup.

## Your first correction

1. **Add a chart photograph.** Check the detected chart face and patch overlay.
   Select another face if the photograph contains multiple charts.
2. **Choose the reference.** Use built-in values, or enter/import your own measured
   values in the chart-reference editor. Review their cell mapping before fitting.
3. **Set the stages.** For uneven lighting, provide a matching blank-field photograph
   and enable **FFC**. Leave correction on **Auto** to start.
4. **Fit and inspect.** Choose **Fit + apply**, compare stage previews, and inspect
   patch errors and held-out validation. A good training score alone is insufficient.
5. **Save and reuse.** Export corrected images and the model JSON. Choose
   **Model → Use loaded** to apply that model to other images from the same setup.

Press **F1** for the searchable offline guide. Preview completion alone does not
export a full-resolution image; use **Save** to choose its destination.

## What you can do

- **Use your own references:** Classic/Passport and SpyderCHECKR 24 at 4×6, and
  Digital SG at 10×14; Lab, XYZ, or supported RGB encodings; manual entry,
  spreadsheet paste, CSV import, and reusable JSON profiles. Assign references
  to individual detected charts.
- **Choose a fitting method:** CCM, polynomial, root-polynomial, PLS, or neural
  network, with saved settings and model reuse.
- **Correct illumination and color:** spatial flat-field correction (**FFC**),
  tone response (**GC**), white balance (**WB**), and color correction (**CC**).
- **Process batches:** fit each chart image or apply one saved model. The existing
  FFC/GC/WB/CC indicators track each image. Turn **Preview results** on or off.
- **Inspect and export:** stage previews, patch errors, reports, corrected images,
  model JSON, and `.cube` LUTs. A cube LUT excludes spatial FFC.
- **Resume work:** save sessions and relink moved images while preserving valid
  completed results and their captured settings.

## Processing and GPU support

**Auto**, **CPU**, and **GPU** select the device for image correction. All use the
combined **65³ GC/WB/CC LUT** by default. Auto selects CPU for small images and GPU
for larger ones; GPU correction retains CPU recovery. Spatial FFC remains separate.

Neural chart and plane detection use GPU inference. Windows Auto prefers
**DirectML**; other routes use **WebGPU/Vulkan**. CPU selection applies to image
correction, not neural inference. Actual availability depends on the driver,
adapter, and platform. [Backend behavior and measured limits](docs/compute/README.md)

For the full FFC/GC/WB pipeline, start with encoded **sRGB/D65** input. Embedded ICC
profiles and EXIF orientation are honored for that input path. Explicit other
encodings require the matching settings; exported pixels need the correct
interpretation downstream. See the [color interpretation guide](docs/user-guide.md#color-interpretation-matters).

## Python API

The optional SDK uses the same native engine without Python ML or GUI
dependencies. Install the bundled wheel from your app's `python/` folder, or
install from this source checkout:

```sh
python -m pip install ./api
```

Set `root` to the installed or extracted application folder. The default Windows
folder is `%LOCALAPPDATA%\Programs\ColorCorrect Studio`; the Debian package uses
`/opt/colorcorrect-studio`.

```python
from colorcorrect import ColorCorrection, Config

with ColorCorrection(Config(save_path="results"), root="/path/to/ColorCorrectStudio") as cc:
    calibration = cc.run(Image="chart.jpg")  # Optional: White_Image="blank.jpg"
    result = cc.predict_images(images=["scene1.jpg", "scene2.jpg"])
    print(calibration.metrics)
    print(result.outputs)
    calibration.export_lut("correction.cube")
```

The native app is **0.2.0**; the independently versioned SDK is **2.2.1**. Its wheel
does not include the native worker or models. For custom references, typed
options, model reload, and partial batch failures, see the [API guide](api/README.md)
and [typed API reference](docs/api/README.md).

## Builds, releases, and development

Updates to `main` automatically build and test Windows/Linux x64 and ARM64
applications and validate Apple-silicon macOS source. Pull requests run the same
native checks. Download development candidates from the
[Actions page](https://github.com/collinswakholi/ColorCorrectionPackage_UI/actions/workflows/native-builds.yml).

A version tag such as `v0.2.0` runs all five checks, then publishes the matching
Windows/Linux assets and checksums to GitHub Releases. Preview releases remain
marked as prereleases. macOS uploads contain text validation evidence only.

Build with the pinned **.NET SDK 10.0.402** and **Rust 1.95.0** toolchains.
[Build instructions](docs/building.md) · [Contributing](CONTRIBUTING.md)
· [Validation status](docs/native-validation.md) · [Migration notes](docs/migration.md)

The application processes still images. Settings and sessions retain the legacy
`CCP-New` data location for compatibility. Keep your models, sessions, and exports
outside the application installation folder.

Application code is [MIT licensed](LICENSE). Bundled models and dependencies have
separate terms; read [third-party notices](THIRD_PARTY_NOTICES.md),
[model notices](models/NOTICE.md), and [model provenance](docs/model-provenance.md).
