"""Real SDK/example integration. Missing native features/fixtures are failures.

Output stays in a unique directory under --output-dir. This suite covers still-image workflows.
"""
import argparse
import csv
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time

API = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(API), str(API / "examples")]
from colorcorrect import Client, BatchProcessingError, InvalidInputError, CalibrationOptions, FFCOptions, ApplyOptions
from workflow import calibrate_builtin, calibrate_custom, apply_batch, reload_and_export


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=API.parent)
    parser.add_argument("--engine", type=Path)
    parser.add_argument("--samples", type=Path, help="Public sample directory; defaults to <root>/test_images")
    parser.add_argument("--output-dir", type=Path, default=API.parent / "_implementation/python-integration")
    args = parser.parse_args()
    root = args.root.resolve()
    samples = (args.samples or root / "test_images").resolve()
    missing = [name for name in ("ColorCard_Big.JPG", "Blank.JPG", "Sample_3572.JPG", "Sample_3573.JPG")
               if not (samples / name).is_file()]
    if missing:
        parser.error(f"Required public fixtures are missing from {samples}: {', '.join(missing)}")
    args.output_dir.mkdir(parents=True, exist_ok=True)
    out = Path(tempfile.mkdtemp(prefix="sdk-", dir=args.output_dir)).resolve()
    chart = samples / "ColorCard_Big.JPG"
    print("Artifacts:", out, flush=True)
    started = time.perf_counter()
    # Derive this test-only CSV from the checked-in reference table, rather than
    # introducing a second independently maintained set of scientific constants.
    text = (root / "engine/crates/ccp_core/src/refs.rs").read_text(encoding="utf-8")
    block = text.split("const CLASSIC_LAB_D50", 1)[1].split("const CLASSIC_NAMES", 1)[0]
    values = [tuple(map(float, match)) for match in re.findall(r"l:\s*([-\d.]+),\s*a:\s*([-\d.]+),\s*b:\s*([-\d.]+)", block)]
    assert len(values) == 24, "Source reference fixture changed; inspect test extraction"
    csv_path = out / "classic.csv"
    with csv_path.open("w", newline="", encoding="utf-8") as stream:
        writer = csv.writer(stream)
        writer.writerow(["L", "a", "b"])
        writer.writerows(values)
    with Client(args.engine, root=root, timeout=600, progress=lambda text: print(text, flush=True)) as client:
        # Reference equivalence requires identical measured cells. Independent
        # detector forwards may differ slightly on the same graphics provider.
        detection = client.detect(chart)
        builtin = calibrate_builtin(client, chart, out, detection=detection)
        custom = calibrate_custom(client, chart, csv_path, out, detection=detection)
        assert builtin.path.is_file() and custom.path.is_file()
        assert custom.metrics is not None
        assert custom.metrics.get("reference_context"), "Custom provenance is missing from report"
        assert [p["measured_srgb"] for p in builtin.metrics["patches"]] == [p["measured_srgb"] for p in custom.metrics["patches"]]
        before = builtin.metrics["delta_e_2000"]["mean_after"]
        after = custom.metrics["delta_e_2000"]["mean_after"]
        assert abs(before - after) < 1e-6, (before, after)
        batch = apply_batch(custom, [samples / "Sample_3572.JPG", samples / "Sample_3573.JPG"], out)
        assert len(batch.outputs) == 2 and all(path.is_file() for path in batch.outputs)
        try:
            custom.apply([samples / "Sample_3572.JPG", out / "missing.jpg"], output_dir=out)
        except BatchProcessingError as exc:
            assert len(exc.result.outputs) == 1 and len(exc.result.failures) == 1
        else:
            raise AssertionError("Partial batch failure was hidden")
        collision_inputs = []
        for directory, filename in (("one", "a.jpg"), ("two", "a.jpg"), ("literal", "a_1.jpg")):
            destination = out / "collision-inputs" / directory / filename
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(samples / "Sample_3572.JPG", destination)
            collision_inputs.append(destination)
        collision_batch = custom.apply([*collision_inputs, collision_inputs[0]], output_dir=out)
        assert len(collision_batch.outputs) == 4
        assert len({str(path).casefold() for path in collision_batch.outputs}) == 4, "Batch outputs overwrite one another"
        assert all(path.is_file() for path in collision_batch.outputs)
        model_path = custom.path
        ffc_model = client.calibrate(chart, white=samples / "Blank.JPG", output_dir=out,
            options=CalibrationOptions(ffc=FFCOptions(method="pls", degree=5), write_output=False))
        assert ffc_model.metrics["do_ffc"] is True, ffc_model.warnings
        ffc_path = ffc_model.path
    with Client(args.engine, root=root, timeout=600) as client:
        model, lut = reload_and_export(client, model_path, out / "correction.cube")
        assert model.metrics is not None and lut.path.is_file()
        ffc_loaded = client.load_calibration(ffc_path)
        ffc_result = ffc_loaded.apply(samples / "Sample_3572.JPG", output_dir=out,
                                     options=ApplyOptions(write_output=False))
        assert not ffc_result.warnings and not ffc_result.outputs, ffc_result.warnings
        assert ffc_result.images[0].ok
        try:
            model.export_lut(lut.path)
        except InvalidInputError:
            pass
        else:
            raise AssertionError("Existing LUT was overwritten without authorization")
    result = {"ok": True, "elapsed_seconds": time.perf_counter() - started,
              "builtin_mean_delta_e": before, "custom_mean_delta_e": after,
              "examples": ["built-in calibration", "custom CSV", "batch", "reload", "LUT", "FFC reload/apply"],
              "scope": "still images", "artifacts": str(out)}
    (out / "verification.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2), flush=True)


if __name__ == "__main__":
    main()
