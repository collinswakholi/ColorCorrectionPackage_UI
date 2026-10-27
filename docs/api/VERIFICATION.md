# Developer validation: Python API and tutorials

This reference is for contributors maintaining the SDK and documentation.
For ordinary use, start with the [Python quickstart](../../api/README.md),
[configuration reference](README.md#configuration-reference), or
[tutorials](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/notebooks/README.md).
Checks cover the legacy Engine transport, typed Client API, and ColorCorrection
workflow. Python ML and GUI packages are not SDK runtime dependencies.

## Component and integration checks

From the source checkout:

```console
python -m unittest discover -s api/tests -p test_*.py
python api/tests/integration_sdk.py --root .
python api/tests/integration_workflow.py --root .
python api/tests/integration_reference_layouts.py --samples path/to/private-fixtures
```

Component checks use isolated native-process fixtures for transport and API
contracts. Photograph integrations use the real worker and the five public
images in `test_images/`; `--samples` selects another directory. They cover
built-in/custom references, stage previews, batch outcomes, reload, LUT export,
and FFC reuse. Missing required inputs or capabilities must fail clearly.

For distribution checks, install the wheel into a clean environment and exercise
the same workflows against the installed or extracted application package.

Use a different working directory, Unicode paths and explicit native package discovery. Confirm actual execution providers, unique output directories, overwrite protection, timeouts, closed-client errors and retained per-image failure results.

The optional `integration_reference_layouts.py` suite requires an explicit
directory containing `synth_cast.png`, `synth_spyder.png`, and `synth_sg.png`.
These generated fixtures are not distributed. Missing files save a skipped
result and exit 2. A complete run covers four reference layouts, including
Passport compatibility, in three import orders using known geometry. It does
not establish photographed-detector accuracy.

Installed application/API checks run in Windows Sandbox and WSL; host checks
cover isolated components and contracts. Platform/package scope is recorded in
the [native validation reference](../native-validation.md) and
[platform notes](../readiness.md). Results apply only to the identified versions
and environments.

## Automated notebook execution

The runnable [configured pipeline](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/api/examples/configured_pipeline.py)
exercises editable FFC/GC/WB/CC options, custom reference provenance, FFC reuse,
partial batch outcomes, a WB application ablation, and LUT export. The
[notebook runner](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/main/api/tests/execute_notebooks.py) executes both tutorial
notebooks with fresh kernels pinned to its own interpreter. It validates SDK
module locations, uses an explicit native app root, and writes executed copies
outside the source notebooks.

Install the application-owned SDK wheel and the pinned tools in
`notebooks/requirements-qa.txt` into a clean Python environment. Copy the runner,
notebooks, example, and public photographs into a separate working directory:

```sh
python -m pip install -r /path/to/notebooks/requirements-qa.txt
python execute_notebooks.py --notebooks-dir ./notebooks \
  --root /path/to/installed-app --samples /path/to/public-test_images \
  --output-dir /path/to/execution-records --reference-csv /path/to/classic-lab-d50.csv
python configured_pipeline.py --root /path/to/installed-app \
  --samples /path/to/public-test_images --output-dir /path/to/example-results \
  --reference-csv /path/to/classic-lab-d50.csv
```

The runner requires a complete reference CSV so that the custom-reference
exercise is included. Public source notebooks retain cleared outputs; executed
copies and their records belong outside the source notebooks.

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
Later application builds have separate package records; this tutorial record
does not establish their installation behavior, ARM GPU support, or macOS runtime
behavior.

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
ran top-to-bottom in fresh kernels. Source hashes at execution matched the
retained executed copies; later prose-only edits preserve their code cells.
Coverage included six CC choices across five families, three
CC neural optimizers, automatic white-plane detection, FFC/GC/WB/CC records and
stage PNGs, custom CSV references, full-resolution batches, partial missing-input
outcomes, saved-model FFC reuse, WB-disabled application, and LUT export. The
FFC tutorial fit used PLS with 32 bins per side and degree 3; these runs do not
claim exhaustive parameter or all-FFC-method coverage. The broader method matrix
is recorded separately in the [native validation record](../native-validation.md).

Package/module hashes were checked before and after execution. No tutorial
runner, notebook kernel, or owned SDK worker remained after cleanup.

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
