# ColorCorrect Studio v0.1.0 — Windows preview

Release notes for the first native Windows preview. Availability and assets are
listed on the [v0.1.0 release page](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/tag/v0.1.0);
an unavailable page means publication is still pending.
Windows 11 x64 only. Unsigned installer: verify its SHA-256 and source commit.
This is the new Avalonia/Rust desktop app, not the older browser/Flask application.
CPU dependencies, samples, offline illustrated guide, SDK 1.1 and notebooks ship
with Setup. CUDA is optional; NN training and regression fitting remain CPU.

Legacy downloads remain available unchanged. Install side-by-side; refit old
pickle models to native JSON. No legacy data migration or HTTP compatibility
service. Linear/wide-gamut exports require declared-space interpretation; no
automatic output ICC generation. This native preview processes **still images
only**; video controls and processing have been removed, and no FFmpeg/ffprobe is
included. Former video API calls return explicit unsupported errors. Model cube
export remains available and excludes spatial FFC.

Download `ColorCorrect-Studio-win-x64.exe`, its `.sha256`, and `build-info.json`
from the same release. The provenance source commit must match the release's
`v0.1.0` tag and the reviewed, merged native source on `main`. Local QA installers
are validation artifacts, not interchangeable release downloads.

Dependency inventory, third-party/model notices, and the upstream detector
license are separate release assets. The application's MIT license does not
relicense those components. No portable ZIP or new Linux/macOS binaries are part
of this preview. See [migration](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/migration.md),
[release gates](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/releasing.md),
and [validation evidence](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/validation-still-preview.md)
for scope and limitations.
