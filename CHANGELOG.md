# Changelog

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
