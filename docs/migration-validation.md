# Native migration validation — 14 October 2026

This evidence concerns the migration branch, not a published release or clean
Windows 11 certification. Source review remains required. The legacy main SHA
was rechecked unchanged at `c790f8af459ca522090bfc92519fdd4398ec7545` before
import; that commit is preserved remotely on `legacy-web` / `legacy-web-final`.

## Regressions and packaging

- .NET SDK 10.0.402: **40 passed** (18 engine reliability, 22 headless UI/decoder/help).
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
