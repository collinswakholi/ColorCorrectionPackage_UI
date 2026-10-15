"""Real-image smoke test for the short API and actual accelerator telemetry."""
import argparse
import json
from pathlib import Path
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from colorcorrect import Engine

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
parser.add_argument("--engine", type=Path)
parser.add_argument("--policy", default="cpu")
args = parser.parse_args()
samples = args.root / "test_images"
folder = tempfile.mkdtemp(prefix="colorcorrect-simple-")
with Engine(args.engine, root=args.root, policy=args.policy) as engine:
    devices = engine.devices()
    calibration = engine.calibrate(samples / "ColorCard_Big.JPG", folder=folder,
        white=samples / "Blank.JPG", model="rpcc2", do_gc=True, do_wb=True,
        do_cc=True, cross_validate=True, phase2_teacher=False)
    assert calibration.model.is_file()
    assert calibration.report["report_json"]["do_ffc"]
    applied = calibration.apply(samples / "Sample_3572.JPG", output_format="png")
    assert all(Path(row["output"]).stat().st_size > 100 for row in applied["results"])
    assert not any(row.get("ffc_skipped") for row in applied["results"])
    print(json.dumps({"folder": folder, "devices": devices,
        "fit_apply_backend": calibration.report.get("apply_backend"),
        "apply_backend": applied.get("apply_backend"),
        "delta_e": calibration.report["report_json"]["delta_e_2000"]}, indent=2))
    if args.policy in ('cuda', 'directml'):
        assert applied.get("apply_backend") == args.policy, f"{args.policy} policy fell back: do not claim GPU execution"
