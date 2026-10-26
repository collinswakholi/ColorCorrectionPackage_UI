# Python API verification

The SDK has no Python ML or GUI runtime dependencies. Verification covers legacy Engine transport, the typed Client API and the familiar ColorCorrection workflow.

Run unit tests with `python -m unittest discover -s api/tests -p test_*.py`. Install the wheel into a clean environment and exercise documented calibration, custom references, FFC, batches, partial failure, model reload and LUT export against the extracted final native package.

Use a different working directory, Unicode paths and explicit native package discovery. Confirm actual execution providers, unique output directories, overwrite protection, timeouts, closed-client errors and retained per-image failure results.

Source examples and photograph integration scripts use the five verified public images in `test_images/`. The optional `integration_reference_layouts.py` suite requires an explicit `--samples` directory containing private generated layout fixtures. Without all fixtures it saves a skipped result and exits 2; this is missing coverage, never a pass. It validates known-geometry reference handling, not photographed detector accuracy.

For this work, installed application/API tests run in Windows Sandbox and WSL; host checks cover isolated transport/contracts. Current results and untested platforms belong in the [readiness ledger](../readiness.md). Historical video/Torch results are retained locally and do not certify the current still-image SDK.

## Installed tutorial acceptance

The runnable [configured pipeline](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/api/examples/configured_pipeline.py)
exercises editable FFC/GC/WB/CC options, custom reference provenance, FFC reuse,
partial batch outcomes, a WB application ablation, and LUT export. The
[notebook runner](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/api/tests/execute_notebooks.py) executes both tutorial
notebooks with fresh kernels pinned to its own interpreter. It validates SDK
module locations, uses an explicit native app root, and writes executed copies
outside the source notebooks. See [setup and commands](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/notebooks/README.md#execute-installed-acceptance-copies).

Do not substitute source import success for installed acceptance. Copy only the
runner, notebooks/examples, reviewed fixtures, and QA dependencies into the guest;
install the package-owned SDK wheel. Record hashes of those inputs and the native
package, actual providers/completed stages, model and output paths, and kernel
cleanup. Keep public notebook outputs cleared so future readers run their own
measurements. The six method choices include two root-polynomial orders, making
five correction families. Short neural budgets are tutorial settings.

Host tests bind documented constructor calls against current public signatures
and validate the standalone example's editable configurations without starting a
native worker. These tests supplement installed execution; they do not establish
color accuracy or GPU support.

## Recorded tutorial runs — 26 October 2026

**Both notebooks and `configured_pipeline.py` passed in Windows Sandbox and WSL.**
These runs used the published **0.2.0** native workers and their bundled **SDK
2.2.1** wheels. No SDK implementation module changed in this documentation work.
UI-repair 0.2.1 candidates have separate acceptance evidence; this record does
not imply their installers, ARM GPUs, or macOS runtime were tested here.

The published worker source is commit
`ee2522eaddb615dcbddc2ea4505e9e435bfc08ae` under
[tag v0.2.0](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/tag/v0.2.0).
The tested distributions and worker identities were:

| Environment | Package / variant | Verified payload | Actual execution |
| --- | --- | --- | --- |
| Windows x64 Sandbox | Public `ColorCorrect-Studio-0.2.0-win-x64-Setup.exe`, installed at `C:\CCPInstalled` | 381 original files; three installed SDK modules match the bundled wheel | DirectML chart/plane inference; wgpu/DX12 correction on RTX 4070 Ti SUPER |
| Ubuntu 24.04 x64 WSL2 | Public `ColorCorrect-Studio-0.2.0-linux-x64.tar.gz` files plus a **separate experimental Mesa Dozen 25.2.8 add-on** | 489 original files and add-on manifest hashes unchanged | WebGPU/wgpu/Vulkan through Dozen and Windows D3D12 on the same GPU |

WSL results apply to that add-on variant, not the pristine public Linux download
or native Linux desktop hardware. CPU correction cases still used GPU chart/plane
inference. Notebook durations include their surrounding guest work and are not
controlled performance comparisons.

| Artifact | SHA-256 |
| --- | --- |
| Windows x64 Setup.exe | `11b1dcf0ed8405dbc82bb762aa7134553d3045c11dfab24d124bd2d1f69e9524` |
| Linux x64 portable archive | `1f41640fa148df8b2b8af4837a378eabec3705082b0be01eed3e85305f2af8a7` |
| Windows `ccp-engine.exe` | `123cb49377f0729e1c542dfbcdc096fff2a75f1cb6fea61f020cf7db7377a4fd` |
| Linux `ccp-engine` | `4f7bedcb5889a15aa34f617dde213abc4d3723a7e4046438ecfdd8d556b12e51` |
| Windows bundled SDK 2.2.1 wheel | `d8d1105f406a8fbef378df866529dfa87f375138efab7a928a51c0ef5df6c6ba` |
| Linux bundled SDK 2.2.1 wheel | `d92a17aaab44d92966a123d796612f984d244b429de0a3f8f9ff156b5f64fe8d` |

The wheel filename in both distributions is
`colorcorrect_studio-2.2.1-py3-none-any.whl`; validate against the wheel belonging
to the selected distribution. The two package builds have distinct wheel hashes.

**Installed locations were asserted, not inferred from successful imports:**

- Windows interpreter: `C:\CCPValidation\QAVenv\Scripts\python.exe`;
  SDK modules: `C:\CCPValidation\QAVenv\Lib\site-packages\`;
  worker: `C:\CCPInstalled\ccp-engine.exe`.
- WSL interpreter: `/root/ccp-ui-docs-20261026/notebook-qa/venv/bin/python`;
  SDK modules: that environment's `lib/python3.12/site-packages/`;
  worker: `/root/ccp-published-020-qualification-20261024/addon-variant/ccp-engine`,
  reached through its package-owned `ccp-engine-launch` wrapper.

The source checkout was unavailable to the guest test processes. Both tutorials
ran top-to-bottom in fresh kernels, and their source hashes matched the retained
executed copies. Coverage included six CC choices across five families, three
CC neural optimizers, automatic white-plane detection, FFC/GC/WB/CC records and
stage PNGs, custom CSV references, full-resolution batches, partial missing-input
outcomes, saved-model FFC reuse, WB-disabled application, and LUT export. The
FFC tutorial fit used PLS with 32 bins per side and degree 3; these runs do not
claim exhaustive parameter or all-FFC-method coverage. The broader method matrix
is recorded separately in the [native validation record](../native-validation.md).

Package/module hashes were checked before and after execution. No tutorial
runner, notebook kernel, or owned SDK worker remained after cleanup; the separate
Windows UI worker was preserved. Private harness incidents were retained and
resolved without modifying the app/SDK: PowerShell variable/process-observer
handling on Windows, and a shell-staging quoting error before WSL execution.

Local raw evidence is retained under the following ignored repository paths;
it is not bundled or uploaded with these guides:

- `_implementation/ui-docs-20261026/sandbox/python-tutorial-summary.json`
- `_implementation/ui-docs-20261026/sandbox/evidence/python-tutorials/complete.json`
- `_implementation/ui-docs-20261026/wsl/notebook-qa/WSL-NOTEBOOK-RESULTS.json`

Those summaries identify the executed copies, output/model paths, module hashes,
loaded libraries, process observations, and detailed success/failure outcomes.
Host component tests passed 51 cases, including documented constructor binding
and editable option examples. This is execution/correctness evidence, not proof
of independent physical color accuracy or untested platform support.
