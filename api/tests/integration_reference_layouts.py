"""Opt-in reference integration using explicitly supplied private sample rasters.

These generated borderless charts test sampling/fitting and all reference layouts;
they do not certify photographed-chart recognition or physical color accuracy.
Requires a real worker and --samples. Private fixtures are not distributed with
the source. Missing fixtures produce a saved skipped result and exit code 2;
processing/hash failures exit 1. Only a complete run exits 0.
"""
import argparse
import csv
import hashlib
import json
from pathlib import Path
import struct
import sys
import tempfile
import traceback
from uuid import uuid4

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "api"))
from colorcorrect import (BackendInfo, CalibrationOptions, Client, DetectedChart,
                          DetectionResult, ReferenceProfile)


def known_geometry(image, chart, rows, columns):
    header = image.read_bytes()[:24]
    assert header[:8] == b"\x89PNG\r\n\x1a\n", f"Expected PNG fixture: {image}"
    width, height = struct.unpack(">II", header[16:24])
    snapshot = str(uuid4())
    chart_id = snapshot + ":known-grid"
    cells = [[[c * width / columns, r * height / rows], [(c + 1) * width / columns, r * height / rows],
              [(c + 1) * width / columns, (r + 1) * height / rows], [c * width / columns, (r + 1) * height / rows]]
             for r in range(rows) for c in range(columns)]
    face = {"chart_id": chart_id, "chart_type": chart, "cli_chart": chart,
            "rows": rows, "cols": columns, "patch_cells": cells}
    handshake = {"ok": True, "snapshot_id": snapshot, "charts": [face], **face}
    info = image.stat()
    return DetectionResult(image, snapshot, (DetectedChart(chart_id, chart, rows, columns, None, {"patch_cells": cells}),),
                           BackendInfo("cpu", {}, {"geometry_source": "known synthetic grid"}), (), {},
                           json.dumps(handshake), (info.st_size, info.st_mtime_ns))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--engine", type=Path)
    parser.add_argument("--samples", type=Path, help="Explicit directory containing the three private synthetic PNG fixtures")
    parser.add_argument("--output-dir", type=Path, default=ROOT / "_implementation/reference-layout-integration")
    args = parser.parse_args()
    root = args.root.resolve()
    args.output_dir.mkdir(parents=True, exist_ok=True)
    output = Path(tempfile.mkdtemp(prefix="layouts-", dir=args.output_dir)).resolve()
    evidence = {"ok": False, "status": "running", "expected_cases": 12,
                "scope": "Generated known-geometry sampling/fitting; no detector or independent physical color claim",
                "cases": []}
    def save():
        (output / "verification.json").write_text(json.dumps(evidence, indent=2), encoding="utf8")
    required = ("synth_cast.png", "synth_spyder.png", "synth_sg.png")
    samples = args.samples.resolve() if args.samples else None
    missing = [name for name in required if samples is None or not (samples / name).is_file()]
    if missing:
        evidence.update(status="skipped", missing_fixtures=missing, skipped_cases=12,
                        reason="Private synthetic fixtures are not distributed. Supply the complete fixture directory with --samples.")
        save()
        print(json.dumps({**evidence, "artifacts": str(output)}, indent=2))
        return 2
    evidence["fixture_directory"] = str(samples)
    try:
        catalog = {row["Chart"]: row for row in json.loads((root / "app/ColorCorrect.Engine/ReferenceTemplates.json").read_text(encoding="utf8"))}
        reviewed = json.loads((root / "docs/compute/reviewed-corpus.json").read_text(encoding="utf8"))
        registered_synthetic = {row["chart"]: row for row in reviewed.get("synthetic_layout_fixtures", [])}
        layouts = (("classic", "synth_cast.png"), ("passport", "synth_cast.png"),
                   ("spyder", "synth_spyder.png"), ("digital-sg", "synth_sg.png"))
        fixture_hashes = {filename: hashlib.sha256((samples / filename).read_bytes()).hexdigest()
                          for filename in required}
        for chart, filename in layouts:
            if chart in registered_synthetic:
                expected_hash = registered_synthetic[chart]["sha256"]
                if fixture_hashes[filename] != expected_hash:
                    raise ValueError(f"Reviewed synthetic {chart} fixture changed: {samples / filename}; expected {expected_hash}, got {fixture_hashes[filename]}")
        options = CalibrationOptions(model="ccm", write_output=False)
        with Client(args.engine, root=root, backend="cpu") as client:
            for chart, filename in layouts:
                image = (samples / filename).resolve(strict=True)
                # Passport profiles intentionally use a Classic-face detection. The
                # source table is Classic here, so equivalence has identical targets.
                detected_template = "classic" if chart == "passport" else chart
                template = catalog[detected_template]
                rows, columns = template["Rows"], template["Columns"]
                detection = known_geometry(image, detected_template, rows, columns)
                baseline = client.calibrate(image, detection=detection, output_dir=output, options=options)
                expected = baseline.metrics["delta_e_2000"]["mean_after"]
                for order in ("row-major", "column-major", "coordinates"):
                    values = template["Lab"]
                    if order == "column-major":
                        values = [values[r * columns + c] for c in range(columns) for r in range(rows)]
                    if order == "coordinates":
                        path = output / f"{chart}-coordinates.csv"
                        with path.open("w", newline="", encoding="utf8") as stream:
                            writer = csv.writer(stream)
                            writer.writerow(["cell", "L", "a", "b"])
                            writer.writerows([f"{chr(65 + i % columns)}{i // columns + 1}", *value]
                                             for i, value in reversed(list(enumerate(values))))
                        reference = ReferenceProfile.from_csv(path, chart=chart)
                    else:
                        reference = ReferenceProfile.from_values(values, chart=chart, order=order)
                    calibration = client.calibrate(image, detection=detection, reference=reference,
                                                   output_dir=output, options=options)
                    actual = calibration.metrics["delta_e_2000"]["mean_after"]
                    difference = abs(actual - expected)
                    assert difference < 1e-5, (chart, order, expected, actual)
                    context = calibration.metrics["reference_context"]
                    assert context["reference_assignments"][0]["chart_id"] == detection.charts[0].id
                    assert context["reference_profiles"][0]["id"] == reference.id
                    assert len(calibration.metrics["patches"]) == rows * columns
                    saved = json.loads(calibration.path.read_text(encoding="utf8"))
                    assert saved["reference_context"]["reference_profiles"][0]["id"] == reference.id
                    evidence["cases"].append({"chart": chart, "order": order, "fixture": str(image),
                        "sha256": fixture_hashes[filename], "patch_count": rows * columns,
                        "mean_delta_e_difference_from_builtin": difference, "model": str(calibration.path)})
        assert len(evidence["cases"]) == evidence["expected_cases"]
        evidence["ok"] = True
        evidence["status"] = "passed"
        save()
        print(json.dumps({"ok": True, "cases": len(evidence["cases"]), "artifacts": str(output)}, indent=2))
        return 0
    except Exception:
        evidence.update(status="failed", error=traceback.format_exc())
        raise
    finally:
        save()


if __name__ == "__main__":
    raise SystemExit(main())
