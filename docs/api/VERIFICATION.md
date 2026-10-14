# Headless API verification — 14 September 2026

These are observed results on the Windows development machine using the rebuilt native worker and CPU policy. They are smoke measurements, not a controlled performance comparison: some QA ran concurrently, and cache state differs between calls. No claim is made that these timings generalize to other hardware or captures.

## Transport and errors

`python -m unittest discover -s api/tests -v`: **11 passed**. Real subprocess pipes (a deterministic fixture worker) verify Unicode/space paths, progress, draining over 1 MB of stderr without deadlock, bounded diagnostic history, timeout termination and restart without a stale reply, invalid JSON/crash recovery, partial-batch errors, graceful shutdown with deferred artifact completion, callback failure recovery, all convenience methods, FFC white requirement and strict NN input validation.

Native regression coverage also verifies that explicit `export_cube` model paths override prior fit state, that video LUTs honor stage switches, and that incompatible video model colorimetry is rejected.

## Real still-image workflow

`python api/tests/integration_workflow.py`: **passed** using `test_images`. Detected `ColorCard_Big.JPG` and `ColorCard_Combo.JPG` (the selected face in both replies was `classic_4x6`; this test does not claim that both are different chart types). Used `Blank.JPG` only as the FFC white. Fitted RPCC2 with PLS FFC degree 5, automatic GC, WB, CC and five-fold held-out validation. Applied the saved model to both `Sample_3572.JPG` and `Sample_3573.JPG`, generated all four stage previews, reloaded the model in a separate worker and exported a 33³ LUT. Confirmed that explicit missing-model export fails instead of silently using a prior fit and that a partly failed batch raises with preserved per-image results.

| Observed operation | Seconds |
|---|---:|
| Detector warmup | 0.524 |
| Big detection | 0.683 |
| Combo detection | 0.648 |
| Fit, report/model and corrected PNG | 2.439 |
| Apply two images with FFC and PNG output | 2.914 |

Fitted-chart mean CIEDE2000 was **7.0467 → 1.3967**; held-out CC validation mean was **2.5759** (24 patches, five folds). Held-out validation uses the preprocessed chart and is not an independent end-to-end capture validation. Applied scene images intentionally have no invented DeltaE values.

## Executed notebooks

`python api/tests/integration_video.py`: **passed**. Generated a one-second 8-bit SDR Rec.709 chart clip and tested automatic keyframe detection/fitting/grading, saved-model grading with all stages disabled (verified all 35,937 LUT entries were identity), and per-frame FFC using Blank.JPG. This is a functional smoke test, not a long-video or codec compatibility benchmark.

Both shipped notebooks were schema-validated, compiled, and executed top-to-bottom in separate fresh Jupyter kernels using an isolated QA environment (Python 3.12, nbformat 5.11.1, nbclient 0.11.0, ipykernel 7.3.0, Pillow 12.3.0). The final rerun included compact real-image thumbnails and verified that graceful shutdown completed every deferred preview PNG. Executed copies and image artifacts were written to temporary folders; delivered notebooks remain clean, without machine-specific output paths or stale execution state.

- `01_headless_workflow.ipynb`: **passed**, 16.57 seconds total; detection, PLS FFC fit, two-image Apply, FFC/GC previews, LUT, report assertions, and original/FFC/corrected image displays.
- `02_compare_fit_methods.ipynb`: **passed**, 23.36 seconds total; all six fit families, then Adam, SGD and RMSprop with `[64,32]`, batch normalization and dropout 0.1, and original/NN-corrected image displays. This notebook uses default polynomial FFC, unlike notebook 01, so errors are not directly interchangeable between notebooks.

| Fit family (notebook 02) | Fit-call seconds | Training mean ΔE00 | Held-out mean ΔE00 |
|---|---:|---:|---:|
| CCM | 2.649 | 2.634 | 3.155 |
| RPCC2 | 1.582 | 1.262 | 2.210 |
| RPCC3 | 1.674 | 0.987 | 2.207 |
| Linear/polynomial | 1.499 | 1.752 | 3.503 |
| PLS | 1.619 | 1.752 | 3.507 |
| NN (200-epoch tutorial budget) | 1.614 | 1.689 | 3.212 |

The three normalization/dropout NN variants yielded held-out means 3.045 (Adam), 3.957 (SGD), and 2.889 (RMSprop). This is one chart with a deliberately short training budget, not a recommended optimizer ranking. Both notebooks explain this limitation and provide extensions for independent captures.

To reproduce notebook QA after installing its optional dependencies:

```powershell
python api/tests/execute_notebooks.py
```

The public SDK itself requires none of those notebook dependencies. See [API guide](README.md) for complete commands and runtime configuration.
