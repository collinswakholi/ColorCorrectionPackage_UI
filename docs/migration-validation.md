# Native migration validation — 14 October 2026

This evidence concerns the migration branch, not a published release or clean
Windows 11 certification. Source review remains required. The legacy main SHA
was rechecked unchanged at `c790f8af459ca522090bfc92519fdd4398ec7545` before
import; that commit is preserved remotely on `legacy-web` / `legacy-web-final`.

## Regressions and packaging

- .NET SDK 10.0.402: **47 passed** (18 engine reliability, 29 headless UI/decoder/help/export).
  Existing Windows visual baselines passed unchanged; none were refreshed.
- Rust 1.95.0: format check, strict all-feature Clippy, and **93 tests passed**.
  Includes 34 published CIEDE2000 vector pairs in both directions, 64 space/white
  identities, reference-table cells, ICC normalization, all EXIF orientations,
  NN optimizers/BN/dropout, geometry, model persistence, and generated SDR video.
- Python SDK 1.1: **13 transport/convenience tests passed**. Restricted sandbox
  subprocess execution stalled; the same suite passed outside it in 2.17 seconds.
- PowerShell parsers and native Git Bash syntax checks passed for build scripts.
- Public source scan passed: five original samples and seven pinned model
  artifacts, no source executables, private machine paths or saved notebook output.
- A self-contained unsigned Windows Setup compiled locally. The payload contains
  app-local CPU LibTorch and MSVC CRT, verified ORT/DirectML/FFmpeg, samples,
  illustrated help, API and notebooks. SHA-256 manifests cover every payload file.
  Local dirty-worktree builds are QA-only and cannot satisfy the release gate.

## Packaged CPU workflows

Tests used the staged application and its bundled CPU runtime with developer SDK
paths removed from PATH. No sibling project or sample junction was needed.
Two chart photographs detected Classic faces; this is not evidence of two
different chart types. Synthetic/reference tests cover additional supported tables
and orientation/profile cases; no physical-chart certification is implied.

RPCC2 with PLS FFC/Blank.JPG, GC/WB and held-out CC: chart mean ΔE00
**7.046657 → 1.396667**, held-out mean **2.575936**. Saved-model application to both
scene images, each stage preview, reload/LUT export and partial-batch failures passed.
Warmup 0.654 s; Big/Combo detections 0.702/0.654 s; fit+PNG 2.869 s; two-image
apply+PNG 3.566 s. These concurrent-QA smoke timings are not speedup claims.

| Method | Full fit + two saved-model applies (s) | Training mean ΔE00 | Held-out mean ΔE00 |
| --- | ---: | ---: | ---: |
| CCM | 6.055 | 2.634092 | 3.155081 |
| RPCC2 | 5.130 | 1.262135 | 2.210245 |
| RPCC3 | 4.994 | 0.987486 | 2.206875 |
| Polynomial (`linear`) | 5.218 | 1.751923 | 3.502656 |
| PLS | 5.317 | 1.751702 | 3.507009 |
| NN (200 epochs, [64,32]) | 6.017 | 1.688592 | 3.211839 |

Every method used the separate Blank.JPG, all four enabled stages, and actual
CPU application telemetry. A missing blank-field path was rejected by the native
worker. Source color math was not altered for this migration.

Generated one-second SDR Rec.709 video passed chart keyframe fit/grade,
saved-model identity switches (all 35,937 cube entries checked), and per-frame FFC.
Both notebooks executed top-to-bottom against the packaged worker in fresh kernels:
01 **14.85 s**, 02 **21.77 s**, including all methods and Adam/SGD/RMSProp with BN
and dropout. Clean source notebooks retain no outputs or private paths.

## Remaining publication gates

Independent fresh checkout at the initial migration commit passed all 40 .NET,
93 Rust and 13 Python tests, with empty compiled-output directories, tracked
samples/models and its own isolated full SDK/runtime stage. Only checksum-verified
dependency download archives were reused; no installed LibTorch/ORT runtime or
cached model path was used. Dependency locks stayed unchanged after publish.

CUDA smoke test reported actual `fit_apply_backend: cuda` and `apply_backend: cuda`.
A stricter DirectML test initially exposed a fixed-17³ ONNX apply graph against
the engine's 33³ LUT. The apply graph was regenerated with symbolic grid sizes;
17³/33³ CPU parity and a real chunk boundary were checked. The repeated DirectML
fit/apply check then reported actual `directml` for both, with chart mean after
1.262135227 versus CPU 1.262135247. NN training remains CPU. DirectML session
options now explicitly follow upstream sequential/no-memory-pattern requirements.
GPU chunk results commit atomically so a later failure cannot double-correct
already-mutated pixels during CPU fallback. This is a release-validation fix,
not a change to fitted color math or reference tables.

Actual Setup installation succeeded into an isolated workspace folder. The first
installed-manifest check caught upstream PDBs excluded by Inno and a missing
FFmpeg checksum file. Staging now excludes debug symbols before hashing and
includes the checksum file. Binary scans also caught local compiler paths in
C++ header assertions; Rust/MSVC release path trimming removes them. Full source
and payload scans include binaries and all model-manifest artifacts. The FFmpeg
PEM parser's string labels were distinguished from actual private-key material.

The Setup payload at `e905345` passed all 545 installed checksums, a CPU workflow
with developer PATH entries removed, and both installed notebooks (12.77/20.00 s).
Scoped reinstall/uninstall passed: its binaries were removed, a user-profile
sentinel survived, and the original settings/session hashes remained unchanged.

The live helper initially had activation/state errors, then recovered. The
installed app passed catalog loading, chart detection overlays, Blank's dedicated
FFC action, all stage previews, Previous/Next browsing through a three-image
session, RPCC2 fitting and saved-model application. With the UI's default FFC
polynomial d3, chart mean ΔE00 was 7.10 → 1.42, held-out mean 2.35; these settings
differ from the PLS FFC benchmark above. A real export outside the installation
folder exposed an unhandled UI-thread exception after confirmation. The command
now retains the Avalonia context, as does corrected-output materialization;
directory/copy errors report failures instead of crashing or silently claiming
success. Four added headless cases cover asynchronous approval/rejection and
directory/individual-copy failure with real UI command bindings. Existing visual
baselines still pass. The live retest then exposed raw RGB stage files being sent
to a standard codec. Stage saving now uses the native RGB decoder, honors PNG/JPEG
for every destination and gives the CC fallback a distinct filename. Two further
regressions check all five image exports, codec signatures, dimensions and pixels.
Explicit Skia encoding also fixes Avalonia's PNG-only `Bitmap.Save` being used
for JPEG destinations, including plot export; a rendered-plot JPEG test covers it.
Repeat live export evidence is attached to the PR; the
complete interactive walkthrough is not marked passed. No captures containing
occluding other apps were saved or uploaded.

The legacy archive tag now has an active, exact-ref GitHub ruleset preventing
updates/deletion with no bypass actors. It still resolves to the original commit.
CI's first full run exposed an unrelated ORT 1.5.2 on the hosted runner's PATH;
both workflows now explicitly load checksum-pinned 1.22.1 from an isolated test
stage. The same dedicated-stage regression rerun passed all 93 Rust tests locally.
The `e905345` read-only PR run then passed on the fresh Windows Server runner,
including actual Setup integrity/uninstall, six methods, video and notebooks.
The export fix is revalidated by the same gates; its final result is on the PR.

Live installed-app, fresh-checkout, available accelerator, reinstall/uninstall
evidence is added to the PR as it is verified. A development host with removed
PATH entries is not equivalent to a clean OS without installed runtimes.
Clean Windows 11 x64/offline/legacy side-by-side validation remains a required
independent gate; Linux/macOS native builds are not validated here.
Complete corresponding source/build material for the GPL FFmpeg full build and
upstream detector redistribution obligations require maintainer review.
The manual workflow will not create a draft until source is merged, gates are
attested, and the corresponding-source archive is pinned. No release was published,
no main merge was performed and no Docker Hub image was changed.
