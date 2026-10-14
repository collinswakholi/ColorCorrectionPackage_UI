# ColorCorrect Studio v0.1.0 — Windows preview

Draft for maintainer review; **not approved for public publication**.
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

Review `build-info.json`, dependency inventory, third-party/model notices and
the migration PR evidence before publishing. No portable ZIP or new Unix binaries
are part of this release. See the repository's migration and release documentation.
