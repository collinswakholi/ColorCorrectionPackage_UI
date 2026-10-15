# ColorCorrect Studio v0.1.0 — Windows 11 x64

The current main release is available on the
[v0.1.0 release page](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/tag/v0.1.0)
and selected by [Latest](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/latest).
Windows 11 x64 only. Unsigned installer: verify its SHA-256 and source commit.
This is the new Avalonia/Rust desktop app, not the older browser/Flask application.
CPU dependencies, samples, offline illustrated guide, SDK 1.1 and notebooks ship
with Setup. CUDA is optional; NN training and regression fitting remain CPU.

Previous browser/Flask releases are retired. Their original downloads remain
available for historical use. Install side-by-side; refit old
pickle models to native JSON. No legacy data migration or HTTP compatibility
service. Linear/wide-gamut exports require declared-space interpretation; no
automatic output ICC generation. This native release processes **still images
only**; video controls and processing have been removed, and no FFmpeg/ffprobe is
included. Former video API calls return explicit unsupported errors. Model cube
export remains available and excludes spatial FFC.

Download `ColorCorrect-Studio-win-x64.exe`, its `.sha256`, and `build-info.json`
from the same release. The provenance source commit must match the release's
`v0.1.0` tag at commit `ccc48920ed46c2145de070b5ecd7147084c368da`.
Later documentation commits on `main` do not change the binary. Local QA installers
are validation artifacts, not interchangeable release downloads.

Dependency inventory, third-party/model notices, and the upstream detector
license are separate release assets. The application's MIT license does not
relicense those components. No portable ZIP or new Linux/macOS binaries are part
of this release. See [migration](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/migration.md),
[release gates](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/releasing.md),
and [validation evidence](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/docs/validation-still-preview.md)
for scope and limitations.

## Release designation

On 15 October 2026 the maintainer promoted the tested public v0.1.0 build from
prerelease to the main (Latest) release. This changes GitHub's designation; the
installer, tag, checksum, and original `build-info.json` remain unchanged. Its
`preview: true` field records the original build status. The installer remains
unsigned and the binary target remains Windows 11 x64.

Installer SHA-256:
`02ee5a0230078ae672ee98432044be96fb7d8f23a113063eed67fc96dac42abf`.

The [release build](https://github.com/collinswakholi/ColorCorrectionPackage_UI/actions/runs/38774895502/attempts/2)
and [final validation evidence](https://github.com/collinswakholi/ColorCorrectionPackage_UI/pull/1#issuecomment-6198637794)
record the tested source, 163 automated tests, live UI/Windows 11 checks,
installed notebook execution, GPU boundaries, and the documented test-runner retry.
