# Engine architecture

ColorCorrect Studio uses an Avalonia desktop interface, a native Rust engine and
an optional Python SDK. The application processes still images and reads
models and sessions saved by Studio 0.1 and later from the `CCP-New` data location. See [building](building.md),
[compatibility](compatibility.md) and the [developer guide](development/README.md).

## Reference profiles

Reference profiles belong to individual detected chart instances. They retain
chart layout, canonical cell identities, encoding, white point, scale and order.
Display swatches use clipped sRGB previews; correction uses unclipped canonical
Lab D50 values derived from the original profile.

Desktop and Python CSV imports accept triplets, a cell label such as `A1` followed
by three values, or 1-based row/column coordinates followed by three values.
Coordinate-labelled rows may arrive in any order. JSON imports reject duplicate
fields. Reusing an unchanged profile keeps its revision; changing values or
metadata creates a new revision. The [user guide](user-guide.md#custom-chart-references)
explains the editor workflow.

## Captured state and persistence

Profiles are snapshots, not references to mutable external files. Batch operations
capture chart selections and reference values before processing. Session files
retain the exact detection snapshot, profile revisions and assignments. Detecting
charts again invalidates existing custom assignments.

Saved models retain calibration provenance and apply without the external profile
file. Spatial FFC still needs its blank-field asset. Editing references requires a
new fit; already saved files remain available. Batch output names stay unique for
repeated inputs and Unicode case variants.

Completed results retain the model, source identity, blank-field identity and
rendering settings that produced them. Preview regeneration and full-resolution
export use that captured state. Missing or changed inputs require recovery instead
of silently substituting the next operation's controls.

## Compute and color evaluation

Inference uses DirectML or WebGPU (Vulkan, Metal). Image correction uses Rust on
the CPU or wgpu with CPU recovery. In fast mode, spatial FFC runs in the same pass
as the combined GC/WB/CC LUT (`ccp_core::fused` on the CPU,
`ccp_detect::ffc_lut_wgpu` on the GPU); Auto times both once and uses the faster.
[Processing options](compute/README.md) describes the user-facing choices.

Existing fold validation measures CC conditional on prior preprocessing and is
labelled **CC-only**. Assessing full-pipeline quality requires independent
photographs and reference values. Accelerator parity measures numerical
equivalence, not physical-chart accuracy. See the
[color-science record](color-science-validation.md) for formulas and reference sources.

The [Python guide](api/README.md) documents the public SDK. The
[developer records](development/README.md#validation-records) cover numerical,
platform and UI coverage separately.

## Engine crates

The worker is a Cargo workspace under `engine/crates`:

- `ccp_core`: color science, chart references, patch sampling, the
  correction maps and the FFC, GC and WB stages. Its `mlp`, `pls` and
  `linalg` modules are private implementation details of the maps and FFC.
- `ccp_detect`: chart and white-plane inference, chart assembly, the
  accelerator probe, and the wgpu LUT and fused FFC + LUT kernels. `ccp_wgpu_infer` is its Linux Rust
  inference fallback.
- `ccp_bridge`: the `ccp-engine` binary. Its library exposes only `run()`,
  the [NDJSON loop](api/protocol.md). Its modules form layers, each using
  only those listed before it: `error`, `request` (typed request fields),
  `work` (the f32 working image), `output` (stills, previews and their
  background PNG encoders), `render` (CPU/GPU LUTs and the record of which
  backend ran), `ffc` (white-field fits and their cache), `model` (the saved
  model file), `charts` (handshake faces and patch sampling), `report`
  (CIEDE2000 metrics), the `fit` and `apply` commands, then `detect_cache`
  and `serve`.
