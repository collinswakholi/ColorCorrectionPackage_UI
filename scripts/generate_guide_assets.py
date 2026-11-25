"""Regenerate the illustrations bundled with the in-app Guide.

Every picture is derived from the public photographs in ``test_images/``:

* ``chart-split.jpg`` / ``sample-split.jpg``: the left half is the photograph
  as captured, the right half is the same pixels after the real correction
  pipeline (FFC -> GC -> WB -> CC, default Root polynomial degree 2) fitted on
  ``ColorCard_Big.JPG`` with ``Blank.JPG`` as the white reference.
* ``blank-field.jpg``: ``Blank.JPG`` lightness with its contrast stretched so the
  light fall-off that FFC removes is visible.
* ``ffc-gain.png``: a smooth gain map computed from ``Blank.JPG`` (a degree-5
  2-D polynomial fitted to L*, like the app's default Polynomial FFC fit).
* ``guide-data.json``: numbers the Guide draws as theme-aware vector graphics
  (stage ΔE00, per-patch colors, neutral lightness, an L* profile across the
  blank field and CIEDE2000 example pairs).

The correction half needs a Studio runtime (the folder holding ``ccp-engine``,
``models/`` and ``refs/``)::

    python scripts/generate_guide_assets.py --root "C:/path/to/ColorCorrect Studio/app"

Requires numpy and Pillow. Outputs go to app/ColorCorrect.App/Assets/Guide.
"""
from __future__ import annotations

import argparse
import json
import math
import shutil
import sys
import tempfile
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

REPO = Path(__file__).resolve().parents[1]
IMAGES = REPO / "test_images"
OUT = REPO / "app" / "ColorCorrect.App" / "Assets" / "Guide"
WIDTH = 560


# ---------------------------------------------------------------- color math
def srgb_to_linear(c: np.ndarray) -> np.ndarray:
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(c: np.ndarray) -> np.ndarray:
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)


M_RGB_XYZ = np.array([[0.4124564, 0.3575761, 0.1804375],
                      [0.2126729, 0.7151522, 0.0721750],
                      [0.0193339, 0.1191920, 0.9503041]])
WHITE_D65 = np.array([0.95047, 1.0, 1.08883])


def _f(t: np.ndarray) -> np.ndarray:
    d = 6 / 29
    return np.where(t > d ** 3, np.cbrt(t), t / (3 * d * d) + 4 / 29)


def _finv(t: np.ndarray) -> np.ndarray:
    d = 6 / 29
    return np.where(t > d, t ** 3, 3 * d * d * (t - 4 / 29))


def srgb_to_lab(rgb: np.ndarray) -> np.ndarray:
    xyz = srgb_to_linear(np.asarray(rgb, float)) @ M_RGB_XYZ.T / WHITE_D65
    fx, fy, fz = _f(xyz[..., 0]), _f(xyz[..., 1]), _f(xyz[..., 2])
    return np.stack([116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz)], axis=-1)


def lab_to_srgb(lab: np.ndarray) -> np.ndarray:
    lab = np.asarray(lab, float)
    fy = (lab[..., 0] + 16) / 116
    fx = fy + lab[..., 1] / 500
    fz = fy - lab[..., 2] / 200
    xyz = np.stack([_finv(fx), _finv(fy), _finv(fz)], axis=-1) * WHITE_D65
    return linear_to_srgb(xyz @ np.linalg.inv(M_RGB_XYZ).T)


def ciede2000(lab1, lab2) -> float:
    L1, a1, b1 = lab1
    L2, a2, b2 = lab2
    c1, c2 = math.hypot(a1, b1), math.hypot(a2, b2)
    cm = (c1 + c2) / 2
    g = 0.5 * (1 - math.sqrt(cm ** 7 / (cm ** 7 + 25 ** 7)))
    a1p, a2p = (1 + g) * a1, (1 + g) * a2
    c1p, c2p = math.hypot(a1p, b1), math.hypot(a2p, b2)
    h1p = math.degrees(math.atan2(b1, a1p)) % 360
    h2p = math.degrees(math.atan2(b2, a2p)) % 360
    dl, dc = L2 - L1, c2p - c1p
    dh = h2p - h1p
    if c1p * c2p == 0:
        dh = 0
    elif dh > 180:
        dh -= 360
    elif dh < -180:
        dh += 360
    dH = 2 * math.sqrt(c1p * c2p) * math.sin(math.radians(dh / 2))
    lm, cmp_ = (L1 + L2) / 2, (c1p + c2p) / 2
    hs = h1p + h2p
    if c1p * c2p == 0:
        hm = hs
    elif abs(h1p - h2p) <= 180:
        hm = hs / 2
    else:
        hm = (hs + 360) / 2 if hs < 360 else (hs - 360) / 2
    t = (1 - 0.17 * math.cos(math.radians(hm - 30)) + 0.24 * math.cos(math.radians(2 * hm))
         + 0.32 * math.cos(math.radians(3 * hm + 6)) - 0.20 * math.cos(math.radians(4 * hm - 63)))
    sl = 1 + 0.015 * (lm - 50) ** 2 / math.sqrt(20 + (lm - 50) ** 2)
    sc = 1 + 0.045 * cmp_
    sh = 1 + 0.015 * cmp_ * t
    rt = (-2 * math.sqrt(cmp_ ** 7 / (cmp_ ** 7 + 25 ** 7))
          * math.sin(math.radians(60 * math.exp(-(((hm - 275) / 25) ** 2)))))
    return math.sqrt((dl / sl) ** 2 + (dc / sc) ** 2 + (dH / sh) ** 2
                     + rt * (dc / sc) * (dH / sh))


def hex_of(rgb) -> str:
    v = np.clip(np.round(np.asarray(rgb, float) * 255), 0, 255).astype(int)
    return "#{:02X}{:02X}{:02X}".format(*v)


# ---------------------------------------------------------------- helpers
def load(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        return np.asarray(image.convert("RGB"), dtype=np.float64) / 255.0


def chart_box(rgb: np.ndarray) -> tuple[int, int, int, int]:
    """Bounding box of the dark chart body on a light background."""
    small_scale = 10
    small = rgb[::small_scale, ::small_scale].mean(axis=2)
    mask = small < 0.22
    rows = np.where(mask.mean(axis=1) > 0.25)[0]
    cols = np.where(mask.mean(axis=0) > 0.25)[0]
    top, bottom = rows.min() * small_scale, (rows.max() + 1) * small_scale
    left, right = cols.min() * small_scale, (cols.max() + 1) * small_scale
    return left, top, right, bottom


def save_jpeg(array: np.ndarray, path: Path, width: int = WIDTH) -> None:
    image = Image.fromarray(np.clip(np.round(array * 255), 0, 255).astype(np.uint8))
    height = round(image.height * width / image.width)
    image = image.resize((width, height), Image.Resampling.LANCZOS)
    image.save(path, quality=84, optimize=True, progressive=True)


def split(before: np.ndarray, after: np.ndarray) -> np.ndarray:
    out = before.copy()
    half = out.shape[1] // 2
    out[:, half:] = after[:, half:]
    return out


def poly_terms(h: int, w: int, degree: int) -> list[np.ndarray]:
    yy, xx = np.mgrid[0:h, 0:w]
    x = (xx / (w - 1)) * 2 - 1
    y = (yy / (h - 1)) * 2 - 1
    return [x ** i * y ** j for i in range(degree + 1) for j in range(degree + 1 - i)]


def poly_fit_2d(z: np.ndarray, mask: np.ndarray, degree: int, scale: int = 1) -> tuple[np.ndarray, np.ndarray]:
    """Least-squares 2-D polynomial; returns the fit on z's grid and on a `scale`x finer grid."""
    h, w = z.shape
    terms = poly_terms(h, w, degree)
    a = np.stack([t[mask] for t in terms], axis=1)
    coef, *_ = np.linalg.lstsq(a, z[mask], rcond=None)
    fine = poly_terms(h * scale, w * scale, degree)
    return sum(c * t for c, t in zip(coef, terms)), sum(c * t for c, t in zip(coef, fine))


def colormap(t: np.ndarray) -> np.ndarray:
    """A short perceptual ramp (deep teal -> sand -> warm), readable in both themes."""
    stops = np.array([[0.11, 0.30, 0.33], [0.25, 0.55, 0.52], [0.80, 0.74, 0.52],
                      [0.90, 0.55, 0.28], [0.78, 0.30, 0.22]])
    t = np.clip(t, 0, 1) * (len(stops) - 1)
    i = np.minimum(t.astype(int), len(stops) - 2)
    f = (t - i)[..., None]
    return stops[i] * (1 - f) + stops[i + 1] * f


# ---------------------------------------------------------------- pipeline
def run_pipeline(root: Path, work: Path) -> tuple[dict, Path, Path]:
    sys.path.insert(0, str(REPO / "api"))
    from colorcorrect import ColorCorrection, Config  # noqa: PLC0415

    with ColorCorrection(Config(save_path=str(work)), root=str(root)) as cc:
        calibration = cc.run(Image=str(IMAGES / "ColorCard_Big.JPG"),
                             White_Image=str(IMAGES / "Blank.JPG"))
        applied = cc.predict_images(images=[str(IMAGES / "ColorCard_Big.JPG"),
                                            str(IMAGES / "Sample_3572.JPG")])
    outputs = {Path(p).name: Path(p) for p in applied.outputs}
    return (calibration.metrics, outputs["ColorCard_Big_corrected.png"],
            outputs["Sample_3572_corrected.png"])


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--root", type=Path, required=True,
                        help="Studio runtime folder containing ccp-engine, models/ and refs/")
    args = parser.parse_args()
    OUT.mkdir(parents=True, exist_ok=True)

    work = Path(tempfile.mkdtemp(prefix="guide-assets-"))
    try:
        metrics, chart_after_path, sample_after_path = run_pipeline(args.root, work)
        chart_before = load(IMAGES / "ColorCard_Big.JPG")
        chart_after = load(chart_after_path)
        sample_before = load(IMAGES / "Sample_3572.JPG")
        sample_after = load(sample_after_path)
    finally:
        shutil.rmtree(work, ignore_errors=True)

    # Before/after: the chart itself, then a real scene corrected by the same model.
    left, top, right, bottom = chart_box(chart_before)
    pad = 60
    crop = np.s_[max(0, top - pad):bottom + pad, max(0, left - pad):right + pad]
    save_jpeg(split(chart_before[crop], chart_after[crop]), OUT / "chart-split.jpg")
    h, w, _ = sample_before.shape
    scene = np.s_[int(h * 0.08):int(h * 0.98), int(w * 0.02):int(w * 0.92)]
    save_jpeg(split(sample_before[scene], sample_after[scene]), OUT / "sample-split.jpg")

    # FFC: lightness of the blank field, its smooth fit, and the gain that flattens it.
    blank = load(IMAGES / "Blank.JPG")
    bh, bw = blank.shape[0] // 200, blank.shape[1] // 300
    small = blank[:bh * 200, :bw * 300].reshape(200, bh, 300, bw, 3).mean(axis=(1, 3))
    lstar = srgb_to_lab(small)[..., 0]
    x0, y0, x1, y1 = metrics.get("ffc_plane", [0, 0, blank.shape[1], blank.shape[0]])
    sx, sy = 300 / blank.shape[1], 200 / blank.shape[0]
    plane = np.zeros_like(lstar, bool)
    plane[int(y0 * sy):int(y1 * sy), int(x0 * sx):int(x1 * sx)] = True
    degree = int(metrics.get("ffc_effective_degree", 5))
    fitted, fine = poly_fit_2d(lstar, plane, degree, scale=2)
    peak = fitted[plane].max()
    gain = np.where(plane, peak / fitted, np.nan)

    # Both pictures show only the white area the app detected (ffc_plane).
    window = np.s_[int(y0 * sy):int(y1 * sy), int(x0 * sx):int(x1 * sx)]
    lo, hi = np.percentile(lstar[plane], [1, 99.5])
    stretched = np.clip((lstar - lo) / (hi - lo), 0, 1)
    gray = np.repeat(stretched[..., None], 3, axis=2)
    save_jpeg(gray[window], OUT / "blank-field.jpg", width=300)

    g = np.nan_to_num(gain, nan=1.0)
    gmax = float(np.nanmax(gain))
    # The picture uses the same fit evaluated on a 2x finer grid, with soft contour lines.
    fine_window = np.s_[int(y0 * sy) * 2:int(y1 * sy) * 2, int(x0 * sx) * 2:int(x1 * sx) * 2]
    fine_gain = (peak / fine)[fine_window]
    heat = colormap((fine_gain - 1) / max(gmax - 1, 1e-6))
    gy, gx = np.gradient(fine_gain)
    slope = np.maximum(np.hypot(gx, gy), 1e-9)
    for level in np.linspace(1, gmax, 7)[1:-1]:
        # Distance to the contour in pixels, so every line is about 1.5 px wide.
        line = np.clip(1.5 - np.abs(fine_gain - level) / slope, 0, 1)[..., None]
        heat = heat * (1 - 0.65 * line) + 0.65 * line
    Image.fromarray(np.round(np.clip(heat, 0, 1) * 255).astype(np.uint8)).save(OUT / "ffc-gain.png", optimize=True)

    row = int((y0 + y1) / 2 * sy)
    cols = range(int(x0 * sx) + 2, int(x1 * sx) - 2, 4)
    measured_profile = [round(float(np.median(lstar[row - 3:row + 4, c])), 2) for c in cols]
    corrected_profile = [round(float(np.median((lstar * g)[row - 3:row + 4, c])), 2) for c in cols]

    # Per-patch colors exactly as the engine reported them.
    patches = [{
        "name": p["name"],
        "measured": hex_of(p["measured_display_srgb"]),
        "corrected": hex_of(p["corrected_display_srgb"]),
        "reference": hex_of(p["reference_display_srgb"]),
        "before": round(p["before_delta_e"], 2),
        "after": round(p["after_delta_e"], 2),
        "measuredL": round(float(srgb_to_lab(np.array(p["measured_display_srgb"]))[0]), 1),
        "correctedL": round(float(srgb_to_lab(np.array(p["corrected_display_srgb"]))[0]), 1),
        "referenceL": round(p["reference_lab_d50"]["l"], 1),
    } for p in metrics["patches"]]

    # CIEDE2000 examples: shift a mid skin tone along b* until the target ΔE00.
    base = np.array([62.0, 14.0, 18.0])
    pairs = []
    for target in [1.0, 2.0, 3.5, 5.0, 10.0]:
        lo_s, hi_s = 0.0, 60.0
        for _ in range(60):
            mid = (lo_s + hi_s) / 2
            if ciede2000(base, base + [0, 0, mid]) < target:
                lo_s = mid
            else:
                hi_s = mid
        shifted = base + [0, 0, (lo_s + hi_s) / 2]
        pairs.append({"deltaE": target, "a": hex_of(lab_to_srgb(base)), "b": hex_of(lab_to_srgb(shifted))})

    de = metrics["delta_e_2000"]
    val = metrics.get("validation_delta_e_2000", {})
    data = {
        "source": "ColorCard_Big.JPG fitted with Blank.JPG as FFC white; scripts/generate_guide_assets.py",
        "model": metrics.get("model"),
        "meanBefore": round(de["mean_before"], 2), "meanAfter": round(de["mean_after"], 2),
        "medianBefore": round(de["median_before"], 2), "medianAfter": round(de["median_after"], 2),
        "maxBefore": round(de["max_before"], 2), "maxAfter": round(de["max_after"], 2),
        "validationMean": round(val.get("mean", float("nan")), 2),
        "validationMedian": round(val.get("median", float("nan")), 2),
        "validationMax": round(val.get("max", float("nan")), 2),
        "validationFolds": val.get("folds"),
        "stages": [{"name": s["name"], "before": round(s["mean_before"], 2),
                    "after": round(s["mean_after"], 2)} for s in metrics["stages"]],
        "patches": patches,
        "ffcGainMax": round(gmax, 3),
        "ffcDegree": degree,
        "profileMeasured": measured_profile,
        "profileCorrected": corrected_profile,
        "deltaEPairs": pairs,
    }
    (OUT / "guide-data.json").write_text(json.dumps(data, indent=1, ensure_ascii=False) + "\n",
                                         encoding="utf-8")
    total = sum(p.stat().st_size for p in OUT.iterdir())
    print(f"Wrote {len(list(OUT.iterdir()))} files, {total / 1024:.0f} KiB, to {OUT}")


if __name__ == "__main__":
    main()
