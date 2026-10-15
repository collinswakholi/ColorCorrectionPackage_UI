# Bundled notices

Build scripts copy full upstream LibTorch, ONNX Runtime, DirectML, .NET and FFmpeg
notices here, plus the licensed VC runtime inventory. The release build also
records locked Rust/NuGet dependencies. These generated files belong in the
installer and release provenance, not as stale copies of a developer's machine.

See [THIRD_PARTY_NOTICES](../THIRD_PARTY_NOTICES.md). Publishing remains gated on
review of the complete FFmpeg corresponding-source material and model rights.
