# Engine architecture

ColorCorrect Studio uses an Avalonia desktop interface, a native Rust engine and
an optional Python SDK. The application processes still images and preserves
legacy models, sessions and the `CCP-New` data location. See [building](building.md),
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

Inference uses DirectML or WebGPU/Vulkan. Image correction uses Rust CPU or wgpu
with CPU recovery; spatial FFC is separate from the combined GC/WB/CC LUT.
[Processing options](compute/README.md) describes the user-facing choices.

Existing fold validation measures CC conditional on prior preprocessing and is
labelled **CC-only**. Assessing full-pipeline quality requires independent
photographs and reference values. Accelerator parity measures numerical
equivalence, not physical-chart accuracy. See the
[color-science record](color-science-validation.md) for formulas and reference sources.

The [Python guide](api/README.md) documents the public SDK. The
[developer records](development/README.md#validation-records) cover numerical,
platform and UI coverage separately.
