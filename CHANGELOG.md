# Changelog

## Unreleased

- Show an error notice instead of closing the app when a settings, help, reference-editor or plot-save dialog fails to open.
- Confirmation prompts: Escape and Cancel dismiss, Enter confirms only non-destructive prompts, and screen readers announce the question.
- Engine protocol 3: every failed worker reply now includes a stable `code` (for example `invalid_model`, `image_too_large`, `no_chart_detected`) next to the unchanged `error` message. The ready event reports `protocol_version` 3; requests and successful replies are unchanged.
- Reject corrupt or crafted saved models with a clear error instead of crashing the engine, and keep the engine running if an unexpected internal error occurs during a request.
- Fix a crash when fitting linear, PLS or NN corrections with polynomial degree 7.
- Reject out-of-range flat-field settings (bins 2–1000, odd smoothing window 1–255, degree 1–10, NN iterations 1–100000) instead of running unbounded fits. Every value the app offers stays valid.
- Refuse images above 200 megapixels (configurable with `CCP_MAX_IMAGE_MEGAPIXELS`) before decoding them, reject empty images, and report 16-bit PNG input as PNG rather than TIFF. Embedded-profile conversion no longer needs a second full-size copy of the image.
- Chart detection reports an oversized image with code `image_too_large` (it previously used `image_decode_failed`), using the same `CCP_MAX_IMAGE_MEGAPIXELS` limit as the other commands.
- Leave chart samples with non-finite values out of correction fits, and fail fits that would produce non-finite coefficients.
- The engine keeps serving after a request line with invalid UTF-8, rejects non-path entries in apply image lists, reports unwritable output and preview folders, bounds its detection cache and removes its temporary detection files on exit.
- Preview PNGs are complete before a request returns, failed preview writes are reported, and at most four preview encodes run at once.
- Python SDK: read and check the worker protocol version (2 and 3 are supported), classify errors by `code` when the worker sends one (exposed as `ColorCorrectError.code`), stop the worker of an Engine that is garbage-collected without `close()`, treat `Shutdown`/`QUIT` like `shutdown`, and accept `setup_probe` in the command-line tool.
- Python SDK: the NDJSON transport (`Engine`, `EngineError`, `Calibration`) moves to its own `colorcorrect_engine` module below the typed workflows, which removes an import cycle; import every public name from `colorcorrect` as before. `Engine.start()` starts the worker eagerly.
- Update the API protocol reference for protocol 3: every command and request field, the ready event, detection snapshot and chart IDs, error codes, and the `auto` default policy.
- Text and status colors meet WCAG AA contrast in both themes (muted text, light focus ring, error/warning/success, ΔE quality ramp), and the "fair" and "warning" ΔE bands are distinct in the dark theme.
- New design tokens for type sizes, corner radii and spacing; views use them instead of fixed values.
- The ΔE, Color Analysis and Chart rendition panels say whether the session is empty, a fit is running, or the last fit failed and why.
- The status bar turns red while the last message is an error.
- Thumbnails and previews decode about 2.5 times faster, at most a few at a time, and unreadable files show "No preview" with the reason instead of a blank tile.
- Long operations are no longer stopped while the engine is still reporting progress; the timeout now applies to silence, with an overall limit of six times the timeout.
- Checking that a loaded model and its white reference are unchanged no longer hashes files on the UI thread while you edit settings; the check runs in the background and again, authoritatively, when Apply or a batch starts.

### Removed (app/engine)

- Saved models from before ColorCorrect Studio 0.1 no longer load: bare correction-model JSON, polynomial gamma (`gc_coeffs` without a `gc_model` curve) and single-hidden-layer neural networks without `layer_weights`. The engine reports `invalid_model` with a message to fit a new calibration. Models saved by 0.1.0 and later are unaffected.
- The engine no longer reads undocumented request aliases that no client sends: `chart_all`, `white_image`/`white_image_path`, `do_sat_extrapolate`, `fit_samples_degree`, `poly_degree`, `nn_tol`, `(nn_)batch_normalization`, `drop_rate`, `rgb_clip_lo`/`rgb_clip_hi`, `ffc.fit_method`, and top-level `ffc_method`/`ffc_tol`/`ffc_cut_off`/`ffc_max_val`. All aliases documented in the protocol reference remain.
- Models and references are only searched for in the shipped layout (`models/opt2`, `models/plane`, `refs/`); the older `deploy_exports`, `chart_detection/` and flat `models/` locations are gone.
- The app no longer honours `COLORCORRECT_EXE` or searches `dist/` folders for the engine; use `CCP_ENGINE_EXE`. The missing-resources error names `CCP_NEW_UI_ROOT`.
- Settings written by 0.1.x are no longer migrated: the pre-layers `HiddenSize`, the duplicate `CpuLut`/`ForceCpu` flags (`LutDevice` carries the choice), and the 0.1.x `cpu` acceleration policy, which now restores as Auto. The app falls back only to `settings.json`, not the pre-release `session.json`, and restored sessions skip unsupported files by format rather than by a retired video flag.
- Removed dead code, unused XAML styles and resources, and the engine's benchmark and diagnostic examples.

### Removed (SDK, scripts, docs)

- Python SDK 3.0.0 (was 2.3.0) needs a Studio 0.2.0 or later worker. A worker whose ready event has no `protocol_version` (Studio 0.1.x) is refused at startup with an error naming Studio 0.2.0; protocol 2 (Studio 0.2.0–0.3.0) and protocol 3 stay supported. `Engine.protocol_version` is `None` until the worker starts.
- SDK: `COLORCORRECT_EXE` is no longer read; set `CCP_ENGINE_EXE` instead.
- SDK: worker discovery looks only in the app folder and `engine/target/release` (plus `ccp-engine-launch` on Linux), no longer in `dist/win-cpu`, `dist/<rid>/payload` or `dist/<rid>`. A macOS `.app` root resolves `Contents/Resources/runtime` only, not `Contents/MacOS`.
- SDK: `load_calibration` accepts saved pipelines only; bare correction-model files (top-level `feature_count` with `coefficients`/`w1`), which no Studio release wrote, are rejected. Detection requires the `snapshot_id` and per-chart `chart_id` that every 0.2.0+ worker sends, and no longer reads a `faces` key or a single-chart handshake without `charts`. `BackendInfo.diagnostics` no longer copies `frames_processed`.
- SDK: custom references and fast mode no longer pre-check `custom_references_v1`, `chart_identity_v1` and `combined_lut65_v1`, which every supported worker declares. The `cpu_inference_v1` and exact-sampling checks remain.
- Remove unused developer scripts (`benchmark_warm_pipeline.py`, `compare_workflow_images.py`, `compute_inventory.py`, `sanitize_model_metadata.py`, `summarize_compute.py`, `ui_inventory.py`, `validate_gpu_corpus.py`, `fetch_onnxruntime.ps1`, `link_samples.ps1`), the one-off public 0.3.0 download check (`verify-public-030.yml`, `verify_public_download.py`) and the 0.2.1 demo capture tooling (`create_demo.py`, its storyboard and manifest). The README demo media stay.
- Remove historical validation records (`docs/readiness.md`, `docs/ui-validation.md`, `docs/validation-windows.md`, `docs/api/VERIFICATION.md`, `docs/development/demo.md`) and trim `docs/native-validation.md` to current procedures. The SDK check instructions moved to the developer guide.

## 0.3.0

- Support reproducible chart fits with an exact random sample count per patch and a seed. Grid sampling remains available for existing recipes.
- Select devices per processing operation and report the execution backend.
- Bundle Python SDK 2.3.0. It checks the native worker capability before requesting exact seeded sampling, so older workers report an unsupported request. Install the wheel from the desktop distribution.

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
