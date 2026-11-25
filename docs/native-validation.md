# Native validation status and acceptance

Developer reference: this page describes where native checks run and what final
acceptance must record. For installation requirements and platform limitations, use
[Compatibility](compatibility.md). Results of earlier candidates are in the Git
history of this page.

Windows/Linux x64 and ARM64 and Apple-silicon macOS are packaging targets. Intel macOS is excluded. Native build/install results, hardware GPU execution and interactive desktop acceptance are recorded separately.

## Where checks run

| Environment | Scope |
| --- | --- |
| Windows host | Unit/component/headless tests, source/package checks, isolated GPU benchmarks |
| Windows Sandbox | Extracted/installed final app, native UI, clean Python wheel and end-to-end workflows |
| WSL with GPU passthrough | Final Linux app/API and WSLg interactions, real adapter/backend evidence |
| GitHub Actions Windows/Linux x64/ARM64 | Native architecture builds (Linux on Ubuntu 22.04), package/install checks, unit/headless tests; usable hardware GPU coverage is separate |
| Bare Ubuntu 24.04/22.04 and Debian 13/12 containers | Fresh-system Linux install: `check-system` fails, `install-dependencies` fixes it, the app reaches "Engine ready" loading only listed libraries |
| Apple-silicon macOS CI | Native application packaging, architecture/runtime and CPU workflow checks; hardware GPU and interactive desktop acceptance remain separate |

Hosted runners may not expose usable graphics hardware. Tests must distinguish unavailable hardware, software adapters and actual hardware execution. CPU correction fallback remains available. Auto records CPU inference recovery; forced GPU inference fails clearly.

## Final acceptance evidence

Record OS/process architecture, GPU/driver, package/source/model hashes, scaling, commands, actual providers and outcomes. Exercise:

- Offline installation/launch and prerequisite detection; missing GPU driver guidance.
- Native file/folder dialogs, drag/drop, Unicode paths, ICC/EXIF and input rejection.
- Every correction/FFC method, stage toggles, custom reference imports/orders, chart selections, stale fits and session/model reload.
- Minimum window, 125/150/200% scaling, keyboard/tab/escape, themes, overlays, plots and save/export dialogs.
- Multiple-image batches, partial failures, cancellation, worker recovery and model reuse with required FFC assets.
- CPU/GPU correction equivalence, real GPU inference, unavailable engines, malformed output and transactional GPU recovery.
- Clean installed Python SDK and documented examples from a different working directory.

Missing fixtures, visual baselines, GPUs or native hosts remain explicit failures/skips. Xvfb, cross-compilation, emulation and a process staying alive cannot substitute for interactive/native GPU acceptance.
