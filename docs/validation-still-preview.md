# Still-image preview validation record

Validation performed on **14 October 2026**. This records observed results, not
certification of every camera, physical chart, GPU, or platform.

## Build identity

The installed application and clean Windows 11 guest tests used source
`293ba941451046d17ba3c36d770e7e29d6407320`, a clean, still-image-only **validation
build**, not a published release.

- Installer: `ColorCorrect-Studio-win-x64.exe`, 294,026,228 bytes.
- SHA-256: `e69a78d5f5f33f83eb061d49a0b7100accd6de88dd7045f5ad68cf8acb9aee46`.
- [Read-only PR validation for that commit](https://github.com/collinswakholi/ColorCorrectionPackage_UI/actions/runs/38653721972): passed.
- .NET 10.0.402, Rust 1.95.0, app-local LibTorch 2.9.1 CPU, ONNX Runtime
  1.22.1, and DirectML 1.15.4. No FFmpeg/ffprobe payload.

Subsequent review changes rewrite the README, remove a stale video-frame tooltip
with a regression assertion, and remove one exact obsolete video test file on
installer upgrades. They do not change correction math. Rebuilds have their own
source identity and checksum; the values above must not be attributed to them.
Only a new build of the reviewed, merged commit can become the draft release.

## Live installed-app walkthrough

The app ran with an isolated QA profile and the five public `test_images` files.
The real user profile was not used. The workbench reported DirectML detection on
an Intel UHD Graphics 770; this does not imply every processing stage used GPU.

Observed successfully:

- Catalog expansion/collapse, public thumbnails, and scrolling sidebar with the
  bottom run action remaining available.
- Classic 24 detection and patch overlay on `ColorCard_Big.JPG` and
  `Sample_3573.JPG`.
- FFC with the separate `Blank.JPG`, automatic GC, WB, and RPCC2 CC fitting.
- Original, FFC, GC, WB, CC, and corrected previews; RGB/Lab hover and magnifier.
- Live fitted-chart mean ΔE00 **7.10 → 1.42**; five-fold held-out CC mean **2.35**.
  These are chart results for this configuration, not independent validation of
  the complete imaging pipeline or a promised result on other photographs.
- PNG export of the full-resolution 6000 × 4000 corrected image, four
  2560 × 1706 stage previews, and JSON report. Decoder checks confirmed actual
  PNG files and distinct stage pixels. Saving previews does not claim they have
  the same resolution as the corrected output.
- Previous/next navigation across two images, correct end-boundary disabling,
  and restoration of the first image's corrected preview.
- Switching to **Use loaded** and applying the fitted JSON model to
  `Sample_3573.JPG` without refitting.
- Light/dark theme rendering and inverse theme icon/label.
- Illustrated offline help, text search for `gamma`, accurate chart-neutral GC
  guidance, and Escape returning focus to the help trigger.
- Correction-settings dialog rendering. A stale tooltip referring to a video
  frame was found and corrected in the follow-up source.

The UI automation helper could not reliably operate some owned settings/combo
and file-picker controls. Therefore **interactive NN parameter editing, session
save/reload file pickers, and every modal action are not marked passed**. Their
automated tests are separate evidence, not substitutes for a complete manual
walkthrough before publication. No video workflow was tested as a supported
feature: it has been removed.

## Installed automation and numerical checks

- Both installed notebooks executed in fresh kernels: the full workflow and
  six fit families plus Adam/SGD/RMSProp, layer arrays, normalization, and dropout.
- The full-workflow notebook used Blank-based FFC, GC/WB/CC, model reuse on two
  public samples, and LUT export. Its configured PLS FFC produced chart mean
  ΔE00 **7.046657 → 1.396667**, with held-out mean **2.575936**. Its configuration
  differs from the live polynomial-FFC run above.
- The native/SDK retired-video contract rejected operations explicitly without
  media tools or output artifacts.
- Suites: **88 Rust, 54 .NET, and 16 Python tests**. The .NET Release suite was
  repeated after the tooltip assertion. All **13 Windows visual baselines**
  passed unchanged; none was refreshed to make a failure pass.
- Source and staged/installed payload scans checked checksums, exact public
  sample corpus, private paths, and compressed detector metadata.

See [color-science validation](color-science-validation.md) for conversion,
CIEDE2000, ICC, EXIF, NN, and model-roundtrip test scope and scientific limits.

## Clean Windows 11 and legacy coexistence

A fresh disposable Windows 11 Enterprise x64 guest (10.0.26100) ran with no
developer tools or enabled IP network adapters. The installed self-contained
GUI started **before** QA Python was staged. Installed tests passed for:

- Real FFC/GC/WB/CC workflow, stage previews, saved-model application, and all
  six fitting methods on public samples.
- Missing/unusable enabled-FFC-reference and missing-chart rejection.
- Explicit unsupported-video behavior.
- Reinstall and uninstall retaining a native user-data sentinel, followed by
  reinstall and GUI startup.

The existing legacy Windows installer was then installed alongside the native
app in the same guest. Distinct installer registry identities coexisted, and the
legacy localhost UI returned HTTP 200. Native uninstall left the legacy binary
checksum, a separate QA retention sentinel, and the running legacy UI unchanged.
This verifies those scoped checks; it is not an audit of every legacy data path.

## Still required before publication

Maintainer review/merge, rebuilding the exact merged source, final installer
integrity and lifecycle checks, and completion of the outstanding manual modal
walkthrough. The first release remains an unsigned Windows 11 x64 preview, first
prepared as a **draft prerelease**, not automatically published. Linux/macOS
native-host validation and binaries are outside this preview's verified scope.
