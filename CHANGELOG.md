# Changelog

## 0.1.0 native preview — unreleased

- Replace browser/Flask source at repository root with reviewed ColorCorrect
  Studio Avalonia/Rust source; preserve history and the legacy branch/tag.
- Pin .NET SDK 10.0.402 / Rust 1.95.0; preserve SDK 1.1 and native NDJSON options.
- Track five public sample images and detector weights without local junctions.
- Separate complete LibTorch build SDKs from runtime-only stages; app-local CPU
  and VC runtime deployment preserves installed CPU/CUDA libraries.
- Verify immutable FFmpeg 9.0 / ORT / DirectML downloads, retain notices and record
  build/model/dependency provenance and payload checksums.
- Illustrated offline help, notebooks, migration/build/security/contribution docs.
- Live release validation found and fixed export UI-thread loss after folder
  confirmation; save failures now report errors without crashing or false success.
- Decode native RGB stage previews for export, honor the chosen PNG/JPEG format
  throughout and avoid colliding corrected/CC destination names.
- Replace automatic release/Docker workflows with read-only PR validation and a
  manual source-matched draft Windows preview workflow. No new Unix binaries.

Earlier browser releases remain available unchanged on GitHub Releases.
