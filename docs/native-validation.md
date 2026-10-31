# Native validation status and acceptance

Developer reference: this page records versioned builds, hardware coverage and
historical results. For installation requirements and platform limitations, use
[Compatibility](compatibility.md).

Windows/Linux x64 and ARM64 and Apple-silicon macOS are packaging targets. Intel macOS is excluded. Native build/install results, hardware GPU execution and interactive desktop acceptance are recorded separately.

## Where checks run

| Environment | Scope |
| --- | --- |
| Windows host | Unit/component/headless tests, source/package checks, isolated GPU benchmarks |
| Windows Sandbox | Extracted/installed final app, native UI, clean Python wheel and end-to-end workflows |
| WSL with GPU passthrough | Final Linux app/API and WSLg interactions, real adapter/backend evidence |
| GitHub Actions Windows/Linux x64/ARM64 | Native architecture builds, package/install checks, unit/headless tests; usable hardware GPU coverage is separate |
| Apple-silicon macOS CI | Native application packaging, architecture/runtime and CPU workflow checks; hardware GPU and interactive desktop acceptance remain separate |

Hosted runners may not expose usable graphics hardware. Tests must distinguish unavailable hardware, software adapters and actual hardware execution. CPU correction fallback remains available. Version 0.2.2 adds visible, recorded CPU inference recovery under Auto; forced GPU inference still fails clearly. Historical results below retain their original policies.

## Public 0.2.0 download qualification

The 24 October 2026 UTC audit downloaded all 17 public release assets and verified
their GitHub digests. All eight application distributions match the tagged native
CI candidates; public, embedded, candidate, and native-installed manifests match.
Static inspection verified 1,736 payload hashes and all four native architectures.
This establishes package identity, not additional GPU or UI execution coverage.

The native ARM64 jobs installed those exact packages and passed component and CPU
SDK checks. Their GPU enumeration returned no GPUs. ARM64 GPU chart/plane inference and native
interactive workflows remain unvalidated; ARM64 downloads remain experimental.

Official Linux 0.2.0 assets omit the optional experimental WSL add-on. A fresh WSL
installation can launch and apply a saved model on CPU, but chart/plane inference on the
tested host requires a separately built Mesa Dozen 25.2.8 component. Acceptance
with that component must be identified as **public payload plus the experimental
add-on**. It does not establish out-of-the-box WSL GPU operation or physical
Ubuntu desktop GPU certification. See the [WSL setup requirements](../installer/README.md#experimental-wsl-gpu-setup).

## Earlier candidate evidence

This section records native 0.1.0 candidates through RC8 and their intervening fixes. It is a historical snapshot, including its component-test counts and resource observations. The [readiness ledger](readiness.md) summarizes the subsequent local 0.2.0 installed acceptance. Native architecture CI results and fixes are recorded in [pull request #4](https://github.com/collinswakholi/ColorCorrectionPackage_UI/pull/4); those build/install/smoke checks remain separate from hardware GPU and interactive UI acceptance.

On 23 October 2026 the corrected Windows/Linux x64 worker and installed wheel passed five isolated suites in each guest: prerequisite/bootstrap, inference, Python API, edge cases and stress. Source checkouts were hidden, working directories were neutral and runtime/model/module paths were package-owned. Windows verified 381 installed package files and three SDK modules; Linux verified 506 files and three modules. Offline Windows installation also exercised the bundled VC prerequisite.

Each guest completed 25 correction/FFC combinations, 100 full-resolution applications across four 24 MP photographs and four additional single-call four-photo batches (CPU/WebGPU × FFC off/on). Corrupt middle inputs retained successful outputs in both continue and exception modes; a subsequent batch recovered. The final corrected-worker soak recorded:

| Guest | Workers | Median RSS change | Handle/descriptor change | Peak RSS |
| --- | ---: | ---: | ---: | ---: |
| Windows Sandbox | 1 | +190,464 bytes | −8 handles | 1,051,373,568 bytes |
| WSL | 1 | −167,936 bytes | 0 descriptors | 1,140,981,760 bytes |

These are bounded observations, not proof against every long-term leak. Timings overlapped other validation/build work and are not controlled performance benchmarks. CPU/GPU comparisons reused fitted models; encoded images differed by at most one channel step. Separate host checks retain the `1e-5` normalized RGB tolerance for identical LUT arithmetic.

Both native UIs repeated the original crash sequence: load a completed session and cached GC preview, move the source, reload the missing-source session, save it unresolved, then relink identical content. Detection and completed results remained; GC and Original previews rendered without termination. An actual NN/FFC fit completed. Cancellation retained completed outcomes and returned the UI to Ready. Restarted 80-item queues and subsequent 20-item queues completed with zero failures in both guests, with responsive preview navigation. These are 100 UI **preview** applications per guest from four real 24 MP sources, with NN FFC and the combined 65³ LUT; full-resolution SDK applications are separate.

The corrected Windows native worker held exactly 466 handles from the warmed first queue through 80 and 100 previews, with 28 live threads. Its census contained 19 active thread handles and one completed encoder, replacing the earlier accumulation of 100 terminated thread handles. WSL ended with one worker, 79 worker descriptors and 173 application descriptors. Saved-session checks verified owned source/model/white hashes and preview assets in both guests. In an earlier WSL removal interaction, deleting one completed tile during an 80-item queue left 79 saved entries; queue and live-session counts are recorded separately rather than claiming an 80-entry saved session.

An isolated WSL GTK picker attempt terminated with exit status 1; later unchanged-configuration retries succeeded. The source now selects Avalonia's bundled managed dialogs only when Linux is identified as WSL, avoiding that native GTK route. A subsequent managed picker opened too wide and did not cancel on Escape. The custom 900×620 DIP window was initially discarded by Avalonia's generic registration because its options service was absent. A regression through actual application startup and `owner.StorageProvider` reproduced the resulting plain window; registering the options before the factory fixes it. The complete 123-test UI suite passes with that real registration path, including nine picker/provider/layout and twelve platform-policy cases. The custom window bounds initial size to its owner and physical working area; unhandled Escape cancels through the provider, while child-handled Escape remains untouched. Native open/save/folder/relink/Unicode/cancel evidence is separate from the preceding 100-preview workload. Native Windows, non-WSL Linux and macOS retain their existing dialog selection. Original failures remain preserved.

The subsequent batch feature reuses the existing stage/job indicators for each current image and the batch bar for the queue, with a captured **Preview results** choice. Preview-off writes full-resolution images into owned run folders for Save without generating or decoding corrected previews; **Show preview** is explicit per result. Worker stage callbacks mark actual work and success: spatial FFC, the effective combined GC/WB/CC LUT pass and applicable writes. Combined stages run together, and progress remains below 100% until the captured outcome is verified. Four new progress regressions and the full 42-test bridge library pass. Installed feature regressions must be associated with the new app/worker identities; the earlier 100-preview/resource evidence is not relabelled as exercising these later controls.

Windows inference covers 15 chart/provider cases and three plane cases; DirectML, WebGPU and Vulkan execute. Linux covers ten chart/provider and two plane cases; WebGPU/Vulkan use the Rust/wgpu Vulkan route through Dozen, reported as Microsoft Direct3D12 (NVIDIA GeForce RTX 4070 Ti SUPER), while DirectML correctly rejects as unavailable. Windows LUT application reports the actual wgpu/D3D12 NVIDIA adapter. Reviewed canonical orientation/cell correspondence and provider geometry comparisons are not independent detector ground truth; rotations and explicit detector-resolution timing are separate host checks.

Package/source checksums, exact build identities and targeted final app-policy tests are retained in the local handoff. Original failed relink, private-runtime, installer-manifest, atomic-save and resource attempts remain alongside their corrections. Earlier successes do not substitute for a regression of the changed component.

A final host repetition exposed intermittent Windows access denial while atomically replacing a just-written model JSON (one failure in 20 repetitions). The SDK now retries only Windows access/sharing errors for five delays totaling 310 ms. A real held-open destination recovers; a real read-only destination still fails, retains its previous bytes and leaves no temporary file. Five focused regressions, the complete 48-test API suite and 100 repeated workflows pass. The final wheel receives targeted installed API/model-save repetition in both guests. That SDK-only change preserves the native binaries; the subsequent worker correction below has its own build and installed evidence. Windows open-file sharing is documented by [Microsoft](https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/FileIO/moving-and-replacing-files.md) and [Python's issue tracker](https://bugs.python.org/issue46003); the original interfering process was not identified.

The preview-thread correction has six release-mode regressions, including four new cases for unique paths, same-path ordering and write failures; they also pass in the subsequent full bridge-library run. Optional PNG failures remain optional; required RGB failures still propagate. No color or inference arithmetic changed. Earlier 108-test workspace-wide Rust totals remain historical. The SDK has 48 host contract tests and the source/packaging suite has 43; .NET engine components have 49 passing tests and one explicit real-worker skip.

These are bounded results for the tested packages and translated GPU environment, not certification of ARM64, physical Linux desktops, other GPU vendors or every native UI control. A fused/autotuned WSL candidate was previously rejected for incorrect detections; its initial success markers do not override those failures. See the [readiness ledger](readiness.md) and [compute behavior](compute/README.md).

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
