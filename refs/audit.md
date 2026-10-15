# Chart reference audit

## September 2026 validation update

The independent same-cell comparison is now complete: Classic/Passport match all 24 November-2014-and-newer CGATS values, Digital SG matches all 140 corresponding SG CGATS values, and Spyder matches all 24 named cells in darktable's after-2018 V2 table. Fixtures are checked into the core tests. No numeric table changes were required. See [the reproducible color-science validation report](../docs/color-science-validation.md) for primary-source links, test coverage and limitations. The historical notes below describe the original import and indexing conventions; their “later audit” action is superseded by that report.

**Current canonical column: Speedy tables** in `Speedy_CC_build_1/src/chart.cpp` (`kClassicLabD50After2014`, `kSpyderLabD50`, `kDigitalSgLabD50`), stored here in Opt2 **row-major**. The UI must not say “X-Rite official.” You pick the audit column (Babel / colour-science / X-Rite / Datacolor) later. A small numeric delta is an edition question; a large delta on one cell and a match on its neighbor is a **shuffle bug** — fix the table, do not average it away.

## Same-index law

Opt2 `assemble_one` emits `patch_cells` row-major (`index = row * cols + col`, row 0 = assembled top). `names[i]`, `reference_lab_d50[i]`, and `patch_cells[i]` are the same physical patch. Fit, ΔE00, GC, and WB all pair `measured[i]` to `ref[i]`.

**A1 is not a universal color.**

| Chart | A1 | Notes |
| --- | --- | --- |
| Classic / Passport | dark skin, top-left | Neutrals are row 4 names `white`…`black` (indices 18–23), not “A1” |
| SpyderCHECKR 24 | white, **bottom-left** (Datacolor row A) | Opt2 top row is D1–D6. Neutrals are **A1–A6** (indices 18–23) |
| Digital SG | border white, top-left of 10×14 | Neutrals are **E5–J5 and E6–J6** only |

## CGATS column-major → Opt2 row-major

BabelColor CGATS lists Classic as A1–A4, B1–B4, … F4 (column-major). colour-science keys must be remapped the same way before any audit ΔE. Speedy already remapped After-Nov-2014 Classic into Opt2 row-major: A1…F1, A2…F4. Do not ingest a CGATS file by “first 24 numbers” without that remap.

Digital SG names are `letter + row` in row-major: A1, B1, … N1, A2, … N10. `index = (row-1)*14 + col` (A = 0).

## Digital SG: interior ramp vs border

The chart has two kinds of near-neutrals. Mixing them is a fit bug.

1. **Border repeats** (evenness / gloss): columns A and N, rows 1 and 10. Many copies of white / mid / black. Do **not** use these for GC/WB. Do **not** select “every low |a*|+|b*| patch.”
2. **Interior gray ramps** (published scale): **E5–J5** (indices 60–65) and **E6–J6** (indices 74–79). Default `refs/neutrals.json`.

**Last 6 of 140** = I10, J10, K10, L10, M10, N10 — the right border of row 10, **not** the gray scale.

## Speedy Lab D50/2° (canonical until you pick a column)

Values copied from Speedy `chart.cpp`. Not claimed as X-Rite official.

### Classic A4–F4 (names white … black, Opt2 18–23)

| Cell | Name | L* | a* | b* |
| --- | --- | ---: | ---: | ---: |
| A4 | white | 95.19 | −1.03 | 2.93 |
| B4 | neutral 8 | 81.29 | −0.57 | 0.44 |
| C4 | neutral 6.5 | 66.89 | −0.75 | −0.06 |
| D4 | neutral 5 | 50.76 | −0.13 | 0.14 |
| E4 | neutral 3.5 | 35.63 | −0.46 | −0.48 |
| F4 | black | 20.64 | 0.07 | −0.46 |

### Spyder A1–A6 (Opt2 18–23)

| Cell | L* | a* | b* |
| --- | ---: | ---: | ---: |
| A1 | 96.04 | 2.16 | 2.60 |
| A2 | 80.44 | 1.17 | 2.05 |
| A3 | 65.52 | 0.69 | 1.86 |
| A4 | 49.62 | 0.58 | 1.56 |
| A5 | 33.55 | 0.35 | 1.40 |
| A6 | 16.91 | 1.43 | −0.81 |

Spyder A1 (96.04 / 2.16 / 2.60) is **not** Classic white (95.19 / −1.03 / 2.93).

### Digital SG E5–J6

| Cell | Index | L* | a* | b* |
| --- | ---: | ---: | ---: | ---: |
| E5 | 60 | 96.78 | −0.66 | 2.01 |
| F5 | 61 | 79.65 | −0.08 | 0.62 |
| G5 | 62 | 65.22 | −0.27 | 0.16 |
| H5 | 63 | 49.71 | 0.12 | 0.69 |
| I5 | 64 | 34.85 | −0.21 | 0.73 |
| J5 | 65 | 15.43 | −0.24 | −0.25 |
| E6 | 74 | 8.07 | 0.12 | −0.93 |
| F6 | 75 | 30.32 | −0.10 | 0.22 |
| G6 | 76 | 39.55 | −0.37 | −0.09 |
| H6 | 77 | 60.04 | 0.09 | 0.05 |
| I6 | 78 | 75.36 | 0.35 | 0.26 |
| J6 | 79 | 88.85 | −0.59 | 0.25 |

## Later audit steps

After remapping each source onto the Opt2 cell of the **same name**, fill per-patch ΔE00 (Classic A4 vs CGATS A4, Spyder A1 vs darktable A1, SG E5 vs BabelColor E5). Until that table exists, keep Speedy numbers and do not label the product as an official X-Rite dataset.
