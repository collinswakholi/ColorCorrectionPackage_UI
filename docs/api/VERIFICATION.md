# Python API verification

The SDK has no Python ML or GUI runtime dependencies. Verification covers legacy Engine transport, the typed Client API and the familiar ColorCorrection workflow.

Run unit tests with `python -m unittest discover -s api/tests -p test_*.py`. Install the wheel into a clean environment and exercise documented calibration, custom references, FFC, batches, partial failure, model reload and LUT export against the extracted final native package.

Use a different working directory, Unicode paths and explicit native package discovery. Confirm actual execution providers, unique output directories, overwrite protection, timeouts, closed-client errors and retained per-image failure results.

Source examples and photograph integration scripts use the five verified public images in `test_images/`. The optional `integration_reference_layouts.py` suite requires an explicit `--samples` directory containing private generated layout fixtures. Without all fixtures it saves a skipped result and exits 2; this is missing coverage, never a pass. It validates known-geometry reference handling, not photographed detector accuracy.

For this work, installed application/API tests run in Windows Sandbox and WSL; host checks cover isolated transport/contracts. Current results and untested platforms belong in the [readiness ledger](../readiness.md). Historical video/Torch results are retained locally and do not certify the current still-image SDK.
