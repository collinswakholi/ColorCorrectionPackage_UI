# Changelog

## 0.2.2

- Add explicit CPU chart and white-plane detection. Auto tries available GPU engines and falls back to CPU when GPU inference cannot complete.
- Add Apple-silicon macOS app bundles with private runtimes, native launchers and the optional Python SDK. Bundles use ad-hoc signing; Developer ID signing and notarization are not included.
- Fix Apple-silicon WebGPU initialization by letting Dawn select its native Metal backend.
- Keep CPU operation available when an enabled experimental WSL GPU add-on is missing or fails its checks, with repair details for failed GPU attempts.
- Add Recommended, CPU-only and Custom setup choices, offline runtime repair and Check setup device diagnostics.
- Report the backend and recovery used by each detection operation, including cached results and saved calibration provenance.
- Improve detection retries, missing-dependency errors and disabled processing controls while preserving completed results after failure or cancellation.
- Clear completed engine warmup progress after changing detection engines, including failed or cancelled restarts.
- Show detection-engine initialization failures without discarding completed results, and keep CPU retry available.
- Fix Python worker shutdown after normal completion, timeouts and interruption, and wait for native worker cleanup.
- Isolate native build outputs so packaging cannot reuse a stale worker from another build.
- Update installation and API guides for the 0.2.2 application and independently versioned 2.2.2 SDK. Keep previous release assets unchanged.

## 0.2.1 — 2026-10-29

- Refresh existing log and ΔE colors when switching themes, preserving completed results.
- Let CC/FFC settings use the available height when resized, while keeping the Done action reachable in small windows.
- Simplify Processing settings with full-width controls, ordinary disclosure rows, scrollable device details, and a visible rounded keyboard focus ring.
- Improve light-theme plot-label contrast, hide the interaction checkbox where the fitting method ignores it, and label the sampling grid in samples per side.
- Run lightweight documentation checks for root README/demo-only edits while retaining native checks for code, packaged documentation, tags and manual runs.
- Rewrite the Python guides and notebooks around the installed SDK, with editable FFC/GC/WB/CC options and clean-environment execution checks.
- Restore research/software attribution and prepare an authentic dark-mode demonstration with expanded analysis figures.
- Wait for owned workers and pending operations before closing the desktop, including Exit, window close, and splash cancellation.
- Publish unsigned Windows/Linux x64 and ARM64 packages from the tagged native builds; retain Apple-silicon macOS source-only delivery and unchanged historical 0.2.0 assets.
- ARM64 graphics/desktop workflows and the separate WSL GPU add-on remain experimental. Signing is deferred to a later release.

## 0.2.0 — Preview release

- Increment the native app and internal Rust packages from 0.1.0 to 0.2.0.
  The independently versioned Python SDK remains 2.2.1; protocol and saved-data
  schemas retain their existing versions.
- Include the native version in Windows installers/portable archives and Linux
  archives/Debian packages, with checks for emitted installer/package metadata.
- Custom chart references and typed/familiar Python calibration workflows.
- GPU inference without Torch; CPU/wgpu correction with combined 65³ LUT default.
- Still-image-only app, clarified processing choices and portable package preparation.
- Model metadata paths sanitized with exact computation equivalence and original backups.
- Windows/Linux x64 and ARM64 build targets; Apple-silicon macOS source-only instructions.
- Current test/package evidence remains separate from historical release notes below.
- Native CI pins Python 3.12.10, locks component restores, reuses release Rust
  builds, checks packaging/source contracts and retains explicit artifact allowlists.
- Build Windows/Linux x64 and ARM64 applications automatically on `main`; publish
  version-tagged preview releases only after all five native validation jobs pass.
  Apple-silicon macOS remains source-only, with text evidence uploads.
- Replace the animated README demo with a static workflow illustration and add
  architecture-specific downloads, installation steps and current API examples.
- Honor cancellation before preparing inputs and move expensive preparation off
  the UI thread; canceled operations retain the previous successful result.
- Capture the selected chart face and rendering inputs before background
  preparation so later control changes cannot alter an operation already started.
- Verify saved result assets and repair interrupted session copies atomically;
  failed recovery preserves the previous session and rejects changed source assets.
- Keep session save/load disk work off the UI thread and guard asynchronous
  completion against changes to the current session.

## Documentation and release designation — 15 October 2026

- Promote the existing v0.1.0 Windows build to the main GitHub release (Latest),
  remove its prerelease flag, and label all 13 browser/Flask releases retired.
  Keep the released installer, tag, checksums, and legacy downloads unchanged.
- Restore an animated native workflow demo and a clearer download, quick-start,
  features, API, and documentation structure to the README.

## 0.1.0 native Windows release

- Replace browser/Flask source at repository root with reviewed ColorCorrect
  Studio Avalonia/Rust source; preserve history and the legacy branch/tag.
- Pin .NET SDK 10.0.402 / Rust 1.95.0; preserve SDK 1.1 and native NDJSON options.
- Track five public sample images and detector weights without local junctions.
- Separate complete LibTorch build SDKs from runtime-only stages; app-local CPU
  and VC runtime deployment preserves installed CPU/CUDA libraries.
- Verify immutable ORT / DirectML downloads, retain notices and record
  build/model/dependency provenance and payload checksums.
- Illustrated offline help, notebooks, migration/build/security/contribution docs.
- Live release validation found and fixed export UI-thread loss after folder
  confirmation; save failures now report errors without crashing or false success.
- Decode native RGB stage previews for export, honor the chosen PNG/JPEG format
  throughout and avoid colliding corrected/CC destination names.
- Remove video controls, processing and FFmpeg/ffprobe dependencies. Keep model
  cube export; former video API names return explicit unsupported errors.
- Reject missing or unusable blank references when FFC is explicitly requested;
  do not silently apply an incomplete pipeline.
- Make NN correction settings independently discoverable in the Windows task
  switcher and inherit the owner's icon, without changing the dialog layout.
- Restore saved correction options and crop when loading a session, including
  NN architecture, optimizer, batch normalization and dropout. Image-only legacy
  session files continue to preserve the current workflow settings.
- Replace automatic release/Docker workflows with read-only PR validation and a
  manual source-matched draft Windows preview workflow. No new Unix binaries.

Earlier browser release assets remain available as retired historical downloads.
