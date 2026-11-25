# Developer guide

For installation and everyday use, start with the [application guide](../user-guide.md).
This page covers source checks, automated builds and implementation records.

## Build and run

Follow the [platform build instructions](../building.md), using the versions in
`global.json` and `rust-toolchain.toml`. Build on the target architecture and keep
the downloaded runtime beside the corresponding worker. The
[engine architecture](../implementation.md) describes reference snapshots and
model/session compatibility; [release procedures](../releasing.md) cover packaging.

The desktop interface uses native Avalonia controls. Headless tests cover its
control tree, keyboard, layout and rendering; native display and hardware coverage
is separate from headless checks.

## Run the checks

Use CPython **3.12.10** for the workflow suite. The pinned PyYAML requirement is
development tooling, not an application or SDK dependency. Prepare the native
runtime as described in the build guide before running integration checks.

```text
python -m pip install --only-binary=:all: --require-hashes -r scripts/requirements-ci.txt
dotnet test app/ColorCorrect.sln -c Release -p:RestoreLockedMode=true
cargo test --release --manifest-path engine/Cargo.toml --locked -p ccp_core -p ccp_detect -p ccp_bridge --no-default-features --features ccp_bridge/ort-plane,ccp_bridge/wgpu-lut,ccp_bridge/rust-inference
python -m unittest discover -s api/tests -p test_*.py
python -m unittest discover -s scripts/tests -p test_*.py
python scripts/check_documentation.py
python scripts/export_source.py --check
```

On Linux, add `-p ccp_wgpu_infer` to the Cargo command for the Linux inference
implementation. CI selects the crates shipped on each platform. GPU-dependent
cases require an available hardware adapter; an unavailable device is not a pass.

## Python SDK checks

The `api/tests/test_*.py` component tests use isolated fake workers for transport
and API contracts. The photograph integrations use the real worker and the public
images in `test_images/` (`--samples` selects another directory):

```text
python api/tests/integration_sdk.py --root .
python api/tests/integration_workflow.py --root .
python api/tests/integration_reference_layouts.py --samples path/to/private-fixtures
```

They cover built-in and custom references, stage previews, batch outcomes,
reload, LUT export and FFC reuse. `integration_reference_layouts.py` needs a
directory with the undistributed generated fixtures `synth_cast.png`,
`synth_spyder.png` and `synth_sg.png`; without them it records a skipped result
and exits 2. It uses known geometry, so it does not measure detector accuracy.
`integration_simple.py --root .` is a short real-image smoke test.

Installed-package suites run in a clean guest (Windows Sandbox or WSL) against the
wheel, never a source checkout: `acceptance_installed.py` (also run by native CI),
`acceptance_edge_cases.py`, `acceptance_inference.py` (copy
`scripts/validate_detector_corpus.py` beside it) and `stress_installed.py`. Each
docstring lists the fixture bundle it needs.

To execute the tutorials, install the application's SDK wheel and
`notebooks/requirements-qa.txt` into a clean environment, copy
`api/tests/execute_notebooks.py`, the notebooks, `api/examples/configured_pipeline.py`
and the public photographs into a separate working directory, then run:

```sh
python execute_notebooks.py --notebooks-dir ./notebooks \
  --root /path/to/installed-app --samples /path/to/public-test_images \
  --output-dir /path/to/execution-records --reference-csv /path/to/classic-lab-d50.csv
python configured_pipeline.py --root /path/to/installed-app \
  --samples /path/to/public-test_images --output-dir /path/to/example-results \
  --reference-csv /path/to/classic-lab-d50.csv
```

The runner starts fresh kernels on its own interpreter, checks that the SDK
modules come from the installed wheel, and writes executed copies outside the
source notebooks. Keep the public notebook outputs cleared.

## Automated builds

Application, API, packaging and bundled-documentation changes on `main` and pull
requests run the five native jobs (Windows and Linux x64/ARM64, Apple-silicon
macOS); Linux jobs run on Ubuntu 22.04 and then install each package on bare
Ubuntu 24.04/22.04 and Debian 13/12 images. A change that touches only the root
`README.md` or `docs/images/demo/` runs the offline documentation job alone;
mixed changes and unrecognized paths keep native validation. The documentation
job checks local links, citations, example syntax and media offline, so a network
failure is never reported as a broken local link.

The aggregate result includes every applicable job, and a later documentation-only
push does not cancel an earlier native build. Main and pull-request builds never
publish; see the [release guide](../releasing.md) for tags and publication.

## Validation records

- [Native acceptance](../native-validation.md): where checks run and what final
  acceptance must record.
- [Color-science record](../color-science-validation.md): formulas, reference
  sources, numerical comparisons and interpretation limits.
- [Model provenance](../model-provenance.md): model hashes, declared terms and
  unresolved training-data provenance.

Counts belong to their recorded source/package identities. Hosted build and
installation checks do not establish interactive UI, hardware GPU or high-DPI
support for every target. Preserve unavailable configurations, failures and
omitted fixtures explicitly rather than inheriting results from an older build.

## Compute measurements

In fast mode with FFC, processing one 24 MP photograph (eight-photo run, RTX 4070
Ti SUPER host) took about 1.07 s in 0.4.0 and takes 0.08–0.14 s in 0.5.0 on the
GPU, where FFC runs fused with the combined LUT. On the CPU it went from 0.90 s
to 0.73 s. The fused output differs from 0.4.0 by at most 1/255 on 0.003% of
pixels. With the device on Auto, the engine times each kernel once against the
CPU on a 2 MP synthetic image and reports the timings in the reply's
`execution.measured`.

These numbers describe one machine and configuration, not a cross-platform
guarantee. New measurements need their own source, package, model and driver
identities. The [processing guide](../compute/README.md) describes the
user-facing behavior.

## Source exports

```text
python scripts/export_source.py --output _implementation/release-readiness/source-export
```

The destination must not exist. The exporter includes current working source,
including untracked additions, hashes copied files, rejects links/private artifacts
and excludes caches. It records the actual Git revision and working-tree state;
a plain source archive cannot supply Git metadata. Build the export from a separate
directory before using it as a distribution input.

The original source reconciliation used upstream revision
`58eedc98a8a333c62658b69f2da4faff5e764d40`. The local archive retains
`_implementation/baseline-source.zip`, `baseline-manifest.json` and
`source-differences-normalized.json`. Public sample photographs and their manifest
provide redistributable inputs. Private photographs and derived reports stay
outside the repository; a missing private input cannot count as completed coverage.

## Packaging

On native Windows 11 x64 or ARM64, stage the pinned runtime and offline Microsoft
Visual C++ prerequisite, then build the matching package:

```powershell
python scripts/fetch_gpu_runtime.py --rid win-x64 --output artifacts/runtime --vc-redist-output artifacts/prerequisites
./scripts/package_windows.ps1 -Rid win-x64 -Runtime artifacts/runtime -VcRedist artifacts/prerequisites/vc_redist.x64.exe -InnoCompiler 'C:\Path\To\ISCC.exe' -FetchNotices
```

On native Ubuntu 22.04 x64 or ARM64 (the oldest supported glibc, as in CI):

```sh
python3 scripts/fetch_gpu_runtime.py --rid linux-x64 --output artifacts/runtime
CCP_ORT_RUNTIME="$PWD/artifacts/runtime" bash scripts/package_linux.sh --fetch-notices
```

Select the native architecture's runtime and toolchain. Windows produces Setup
and portable ZIP files; Linux produces a Debian package with declared system
dependencies and a portable archive. The SDK wheel has its own version.

The explicit notice-fetch option retrieves license text from the exact upstream
commits declared by active dependencies. Offline builds can use a previously
collected and verified `--dependency-notices` inventory.

The optional WSL x64 component is built with
`scripts/build_wsl_addon.sh NEW_DIRECTORY`; pass its `addon` folder to
`package_native.py --wsl-addon`. Mesa 25.2.8 uses a relative ICD path. Preserve its
source hashes, build settings, dependencies and notices. WSL host components
`libd3d12.so`, `libdxcore.so` and `/dev/dxg` remain external prerequisites.
Rebuild reproducibility and hardware correctness belong to the add-on's own
versioned record. Official Linux release assets do not include it.

The [release guide](../releasing.md) specifies filenames, manifests, checksums
and tag-only publication. The optional hardware workflow needs a provisioned
runner and an explicit dispatch.
