# Custom references and portable compute implementation

## Source baseline

GitHub baseline: `collinswakholi/ColorCorrectionPackage_UI` commit
`58eedc98a8a333c62658b69f2da4faff5e764d40`.

The original local directory had no Git metadata. `_implementation/baseline-source.zip`
preserves its text sources before implementation; `baseline-manifest.json` records
SHA-256 for source, models and included assets. The normalized comparison inventory
is `_implementation/source-differences-normalized.json`. No commit or push was made.

Reconciliation preserves the current still-image application, correction defaults, built-in references and legacy model/session behavior. Torch dependencies and video processing were removed by explicit request. Toolchain pins and dependency locks remain reproducible. See [building](building.md) and [compatibility](migration.md).

## Development

On this workspace, use `_implementation/dotnet/dotnet.exe` in place of `dotnet`
when building/testing, or install the SDK pinned in `global.json` normally.

```powershell
& ./_implementation/dotnet/dotnet.exe build app/ColorCorrect.sln -c Release
& ./_implementation/dotnet/dotnet.exe test app/ColorCorrect.sln -c Release
```

The GUI's **Correction settings → Chart references…** opens the editor after a
chart is detected. Choose the detected chart, interpretation and scale, then enter
the three channels per cell. Paste/CSV import has a mapping review. JSON profiles
retain their encoding, white, scale and order. **Copy built-in values** is explicit;
a new custom profile starts empty. Assigning changes requires a new fit.
The editor stays open after assignment so other charts' drafts remain available.
Assign each edited chart individually, then choose **Done** to close it.

Cell coordinates use column letters and numbered rows (A1, B1, …, A2, …), while
patch names are the chart's canonical labels. The photographed overlay highlights
the focused cell. Display swatches are clipped sRGB previews; the engine derives
unclipped canonical Lab D50 values from the original source profile.

Desktop and Python CSV import accept the same three formats: triplets, a cell
label such as `A1` followed by three values, or 1-based row/column followed by
three values. Coordinate rows may arrive in any order. Editing a profile detaches
stale result/save associations while retaining already saved files. Batch jobs
capture chart selections and reference snapshots before processing the queue.
JSON imports reject duplicate fields, including duplicate white points. Reusing
an unchanged profile keeps its revision; editing its values or metadata creates
a new revision. Batch output names reserve literal filename suffixes and remain
unique for repeated inputs and Unicode case variants.

Profiles are snapshots per chart instance, never references to mutable external
files. Redetection invalidates custom assignments. Sessions keep the exact detection
snapshot, profiles and revisions. Saved models keep calibration provenance and
apply without the source profile; spatial FFC still needs its blank-field asset.

The Python SDK and runnable examples are documented in [the Python guide](api/README.md).

## Validation boundaries

Current local results and outstanding acceptance gates are recorded in
[Windows implementation validation](validation-windows.md).

Existing fold validation measures CC conditional on prior preprocessing; it is
labelled **CC-only**. A full-pipeline quality claim requires independent photographs
and independently specified reference truth. Accelerator parity is a numerical
equivalence check, not physical-chart certification.

Production inference uses DirectML or WebGPU/Vulkan; correction uses CPU or wgpu with CPU recovery. [Current compute behavior](compute/README.md) distinguishes final routes from discarded experiments. Windows/Linux x64 and ARM64 are package targets. Apple-silicon macOS is source-only; Intel macOS is excluded. Native support requires recorded acceptance evidence.
