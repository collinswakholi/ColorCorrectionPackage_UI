# API, guide and CUDA revamp — 14 September 2026

## Scope

- Additive Python SDK 1.1 `Engine.calibrate` and `Calibration.apply`; all previous methods and worker options remain available. Unique run/output directories avoid default overwrite. Explicit artifact paths are still honored. FFC white, nested FFC settings and crop rectangle are retained for apply.
- Embedded guide screenshots and a labeled neutral-row schematic; a separate FFC illustration explains the blank field. The schematic is not a printable target or numeric reference table.
- Offline Study PDF button with an honest missing-file explanation. Publisher direct PDF downloads returned HTTP 403; see [offline study instructions](research/README.md). No paper was fabricated or bundled.
- CUDA installs in `%LOCALAPPDATA%\CCP-New\libtorch-cuda`, never the active bundled CPU folder. The successful download action switches the current app's worker environment before restart. Explicit environment overrides otherwise win; discovery prefers a valid add-on, then bundled CPU. A failed install does not erase CPU. Existing invalid destinations are refused instead of recursively deleted.

## Evidence

- Python transport/convenience suite: 13 tests, including old methods, option forwarding, automatic detection, unique paths, retained FFC and partial failure.
- Historical Rust core/bridge: 58 tests passed, including the then-current regressions in [color-science validation](color-science-validation.md). ICC normalization was subsequently added in October; HDR/unbounded RGB and physical-chart certification remain limitations. See migration evidence for current-source results.
- Real short API test: `python api/tests/integration_simple.py` and `--policy cuda`, using the supplied GitHub corpus and Blank.JPG for FFC. Both produced model, chart/report and corrected scene files.
- CPU mean chart ΔE00: 7.046658 → 1.262136. CUDA: 7.046377 → 1.262074. These are independent detections; small differences are expected. They are smoke tests, not a speed benchmark or proof of generalization.
- CUDA installer script used its cached official 2.9.1/cu126 archive, checked the pinned SHA-256, extracted, and staged successfully. A fresh multi-GB network transfer and the interactive download-button click were not repeated; the button-to-service-to-script wiring was inspected.
- Runtime telemetry: LibTorch CUDA active for detect/apply; plane crop DirectML; actual fit/apply replies both `apply_backend: cuda` on RTX 4070 Ti SUPER. NN training, regression fitting and CPU preprocessing do not become CUDA operations. Auto deliberately chooses CPU for still-image LUT application; explicit CUDA opts into GPU LUT execution.

## Reproduce

```powershell
python -m unittest discover -s api/tests -p test_client.py
python api/tests/integration_simple.py --policy cuda
dotnet test app/ColorCorrect.sln -c Release
cd engine
cargo test -p ccp_core -p ccp_bridge --lib --tests --locked
```

CPU and CUDA use the same color-science implementation. A device listed as available is not evidence that an operation ran there: inspect response `apply_backend` and the detector report. CUDA can fall back if unavailable; the CUDA smoke test explicitly fails if the actual apply backend is not CUDA.
