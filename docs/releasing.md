# Release preparation

Build readiness, native validation and publication are separate states. Current work does not commit, push, run remote workflows, sign or publish.

1. Verify the source export, model/runtime hashes, required notices and dependency architecture. Build from that exported source without private junctions or developer runtime fallbacks.
2. Run host component tests and final installed application/API tests in Windows Sandbox and WSL. Retain failures as well as accepted results; record exact package/source/GPU/driver identities.
3. Use Windows/Linux x64 and ARM64 build workflows to produce unsigned validation artifacts. If a hosted runner has no usable GPU, record that limitation and test clear failure; do not count software adapters as hardware GPU acceptance.
4. Review native UI, high-DPI, clipboard/import, FFC, batch/cancellation/recovery and offline dependency behavior on the target architecture. A build, Xvfb run or emulated process is not full native acceptance.
5. Complete runtime/model redistribution notices and review disclosed provenance gaps before public distribution. Apple-silicon macOS remains source-only; no macOS installer or archive is prepared.
6. Only after maintainer authorization, prepare commits/pushes and any separate signing/publication procedure. CI must not publish releases automatically.

Do not remove original user files, private research or raw diagnostic evidence during source cleanup. A dry-run cache inventory is available through `scripts/cleanup_local.py`; essential tests remain in the repository.
