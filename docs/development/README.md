# Developer guide

For installation and everyday use, start with the [application guide](../user-guide.md).
This page covers source checks, automated builds and implementation records.

## Build and run

Follow the [platform build instructions](../building.md), using the versions in
`global.json` and `rust-toolchain.toml`. Build on the target architecture and keep
the downloaded runtime beside the corresponding worker. The
[engine architecture](../implementation.md) describes reference snapshots and
model/session compatibility; [release procedures](../releasing.md) cover packaging.

The desktop interface uses native Avalonia controls. Its control-tree, keyboard,
layout and rendering coverage is recorded in the [UI matrix](../ui-validation.md).
Native display and hardware coverage is separate from headless checks.

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

## Automated builds and documentation edits

Application, API, packaging and bundled-documentation changes on `main` and pull
requests run native Windows/Linux x64 and ARM64 builds and Apple-silicon source
validation. Manual runs and version tags retain the full native workflow.
Main-branch and pull-request builds do not publish releases.

Root `README.md` and dedicated demo-only changes run the lightweight documentation
job without rebuilding applications. Mixed changes, other documents, application
code and unrecognized paths retain native validation. The documentation job checks
local links, citations, example syntax and media metadata offline. A failed network
request is not treated as a broken local documentation link.

The aggregate result includes every applicable job. A later documentation-only
push does not cancel an earlier native build. Publishing requires an exact version
tag and successful native jobs; see the [release guide](../releasing.md).
Apple-silicon CI uploads text reports only, never application or runtime binaries.

## Validation records

- [Platform record](../native-validation.md): operating systems, architecture,
  installed-package workflows and hardware limits.
- [UI matrix](../ui-validation.md): individual controls and observed interactions.
- [Readiness history](../readiness.md): versioned results and earlier corrections.
- [Color-science record](../color-science-validation.md): formulas, reference
  sources, numerical comparisons and interpretation limits.
- [Model provenance](../model-provenance.md): model hashes, declared terms and
  unresolved training-data provenance.
- [Demo production](demo.md): capture sources, encoding and media reproduction.

Counts belong to their recorded source/package identities. Hosted build and
installation checks do not establish interactive UI, hardware GPU or high-DPI
support for every target. Preserve unavailable configurations, failures and
omitted fixtures explicitly rather than inheriting results from an older build.

## Historical compute measurements

Earlier single-host 24 MP measurements recorded about **0.737 s GPU** and
**0.764 s CPU** for combined-LUT processing, versus **1.628 s** for the previous
GPU method. Identical LUT arithmetic differed by at most **5.96e-8** normalized
RGB; LUT approximation was assessed separately. These observations describe that
machine and configuration, not a cross-platform speed guarantee.

Windows DirectML was faster on the recorded NVIDIA hardware than the native
WebGPU/D3D12 and Vulkan alternatives. The WSL Rust/wgpu route avoids a required
drawing feature missing from the recorded Dawn/Dozen combination. Fusion and
autotuning were disabled after incorrect photographic detections were found.
A subsequent WSL comparison matched canonical cells within **0.0001 model pixel**
over **36** reordered-photo detections and completed installed app/API workflows.
The Dozen version, host driver and hardware are part of those results; they are
not a claim about every Linux driver or independent detector ground truth.

Raw timing blocks, commands, rejected trials and historical runtime comparisons
remain in the local research archive. New measurements need their own source,
package, model and driver identities. See the [processing guide](../compute/README.md)
for current user-facing behavior.

## Source exports and history

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

On native Ubuntu 24.04 x64 or ARM64:

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

The [release guide](../releasing.md) specifies distribution filenames, manifests,
checksums and tag-only publication rules. A main-branch build produces unsigned
development downloads, not a published release. The optional hardware workflow
requires a provisioned runner and explicit dispatch. Apple-silicon source jobs
upload text records only; macOS binaries are not distributed.
