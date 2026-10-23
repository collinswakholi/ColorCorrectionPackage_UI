# Color science validation

Audit date: 14 September 2026. These are numerical software checks, not a certification of a physical chart, camera, monitor, or lighting setup.

## Findings and fixes

The CIEDE2000 formula itself passed independent published tests. The report wiring did not: it decoded every input and corrected result as sRGB, including linear and wide-gamut RGB. Outer cross-validation had the same error. Both now decode the declared RGB space, convert to XYZ, Bradford-adapt to D65, and compare against the same chart cell's D50 reference adapted to D65. Evaluation white is consistently D65 before/after, not whichever output white happens to be selected.

Reproduction: an identical reference color Lab D50 (60, 5, −5), represented in linear-sRGB, previously appeared to have **ΔE00 28.06037893**. Correct space-aware scoring gives **less than 1e−10**. This is a metric interpretation fix, not a claimed improvement in camera accuracy.

Bradford's inverse now uses full double precision rather than seven-decimal coefficients. This eliminates avoidable adaptation round-trip drift. Reference values and fitted models were not replaced.

FFC, GC and WB are implemented in encoded sRGB/D65. Previously the app silently accepted other input encodings for those stages. Run, saved-pipeline LUT and image-apply paths now reject unsupported combinations with an actionable error. For other inputs, disable FFC, GC and WB and use CC only, or convert the source pixels to sRGB/D65 first. sRGB/D65 input can use those stages and then CC to another output encoding.

## Three layers of verification

1. **Independent truth:** all 34 supplementary Sharma/Wu/Dalal pairs, in both directions, agree within 0.00005 ΔE00 (the published values have four decimal places). This includes zero chroma, hue-wrap/discontinuity cases and dark colors. [Author's paper and dataset](https://hajim.rochester.edu/ece/sites/gsharma/ciede2000/).
2. **Conversions and identities:** published matrix coefficients for six nonlinear/linear-primary families; ten transfer-function vectors; black, white and transfer breakpoints; XYZ/Lab and RGB/XYZ round trips; Bradford white round trips; identical-color comparison for all **8 RGB spaces × 8 selectable whites**.
3. **Integration:** report before/after scoring for **8 × 8 input/output space pairs** at their nominal whites; five-fold held-out linear fits on 30 interior-gamut colors for each of eight outputs, maximum ΔE00 below 0.001; stage safety checks for every space/white combination; existing fit/apply/report tests.

Implementation fixtures and tests are in `engine/crates/ccp_core/tests/color_validation.rs` and `fixtures/`; bridge integration checks are in `run_pipeline.rs`. Run from `engine`:

```powershell
cargo test -p ccp_core -p ccp_bridge --lib --tests --locked
cargo test -p ccp_core --test color_validation -- --nocapture
```

Baseline before changes: 33 core tests passed. After changes: 41 core tests and 17 bridge tests passed in the non-optional-feature build. Feature-specific packaging tests are separate.

## Reference tables: names matter

| Chart | Independent comparison | Result |
| --- | --- | --- |
| Classic and Passport | X-Rite November-2014-and-newer CGATS hosted by BabelColor; remapped from column-major by cell name | All 24 values each match exactly |
| Digital SG | Corresponding SG November-2014-and-newer CGATS; remapped by A1…N10 names | All 140 values match exactly |
| SpyderCHECKR 24 | darktable's independently maintained after-2018 V2 named-cell table, with Datacolor's color-data source checked for provenance | All 24 values match exactly |

[Classic CGATS](https://babelcolor.com/index_htm_files/ColorChecker24_After_Nov2014.txt), [SG CGATS](https://babelcolor.com/index_htm_files/ColorCheckerSG_After_Nov2014.txt), [darktable reference implementation](https://github.com/darktable-org/darktable/blob/master/src/common/colorchecker.h), [Datacolor reference sheet](https://www.datacolor.com/spyder/downloads/SpyderCheckr_Color_Data_V2.pdf).

The Spyder numeric fixture was extracted from darktable, not OCR of the manufacturer PDF; the audit does not claim an independent spectrophotometric measurement. Existing tests additionally verify correct neutral indices: Classic/Passport 18–23, Spyder A1–A6 (also 18–23), SG interior E5–J5 and E6–J6 rather than border repeats.

Use the correct chart edition. Classic and SG colors changed in November 2014, and SG's superficially similar center patches are not interchangeable with Classic. Physical aging, gloss, illumination and measurement geometry remain important. [BabelColor reference and edition notes](https://babelcolor.com/colorchecker-2.htm), [SG guidance](https://babelcolor.com/colorchecker-3.htm).

## Space definitions and limits

RGB selections describe **pixel encodings**, not aesthetic targets. sRGB/Display P3 use the sRGB piecewise transfer, Adobe RGB uses gamma 563/256, Rec.2020 uses its SDR transfer, ProPhoto uses its linear toe plus gamma 1.8, and the linear variants have no transfer exponent. Matrices derive from each space's primaries and the selected white. [ICC sRGB definition](https://registry.color.org/rgb-registry/srgb), [W3C conversion reference](https://www.w3.org/TR/css-color-4/#color-conversion-code).

Nominal whites are D65 for sRGB, P3, Adobe RGB and Rec.2020; D50 for ProPhoto; ACES white (labelled D60) for ACEScg. ACEScg uses AP1 primaries. Selecting another white creates a custom white-scaled primary space, not a standard ICC encoding. [ACEScg specification](https://docs.acescentral.com/encodings/acescg/).

Compatibility note: the existing engine retains D65 xy=(0.31271, 0.32902), whereas normative RGB standards commonly round D65 to (0.3127, 0.3290). Published-matrix comparisons allow 0.0002 absolute coefficient tolerance for that difference and reference rounding; internal round trips use 1e−12. Changing this silently would change existing saved-fit interpretation. This is documented numerical convention, not exact ICC conformance.

The engine clamps RGB to [0,1]; it is not an unbounded scene-linear ACES/HDR pipeline. Out-of-gamut reference colors may be clipped on encoding and legitimately retain nonzero ΔE after correction. Do not force those values to zero or substitute clipped display swatches for Lab reference truth.

October 14 update: embedded ICC profiles are now converted to working sRGB for sRGB/D65 input, using moxcms; explicit non-sRGB/custom-white inputs bypass this conversion and must match stored pixels. Untagged inputs are still assumed to use the selected encoding. Original previews/thumbnails honor ICC profiles and EXIF orientation; detection and correction share the same oriented loader. The attached Display P3 PNG agrees with an independent Pillow/LittleCMS sRGB conversion within one byte per channel. All eight EXIF orientations in both TIFF byte orders have regression tests. Refit older models trained on incorrectly interpreted tagged input.

Wide-gamut/linear exported bytes still require matching interpretation downstream; these paths do not generate a matching ICC profile automatically. Corrected/stage worker previews are converted to display sRGB separately from numeric export before 8-bit quantization. A regression verifies linear output byte 55 remains 55 while its preview is 128. No preview substitutes for a calibrated wide-gamut display proof. Legacy `*_srgb` fields remain compatible; `colorimetry` identifies actual encodings and comparison conditions, and `input_normalization` records normalization policy.

Low training ΔE is not proof of generalization. Prefer held-out scores and a separate photographed validation chart. No chart means no invented ΔE. Blank.JPG is the FFC white reference, never a color-chart truth fixture.

For swatches and display plots, each report patch also provides `measured_display_srgb`, `reference_display_srgb`, and `corrected_display_srgb`. All three are encoded display sRGB/D65, independent of selected input/output encoding. The raw legacy fields are preserved for API compatibility; `reference_srgb` has always been display sRGB derived from the canonical D50 Lab reference. All 64 input/output report cases verify identical display colors and unchanged raw fields.
