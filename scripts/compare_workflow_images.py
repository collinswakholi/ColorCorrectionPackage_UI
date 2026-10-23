"""Untimed decoded-PNG comparisons for shared-model application measurements.

The floating-point identical-LUT 1e-5 requirement is tested separately. Exported
8-bit pixels can differ by one quantization step when float rounding straddles a
boundary. Fresh-detection/full-fit workflows are deliberately not pooled here.
"""
from __future__ import annotations
import argparse
import json
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    import numpy as np
    from PIL import Image
    folder = args.directory.resolve()
    runs = json.loads((folder / "runs.json").read_text(encoding="utf-8"))
    rows = []
    for scenario in ("apply", "batch"):
        selected = [run for run in runs if run["scenario"] == scenario]
        cpu = next((run for run in selected if run["requested_policy"] == "cpu" and run["status"] == "measured"), None)
        if cpu is None:
            rows.append({"scenario": scenario, "passed": False, "error": "Missing successful CPU baseline"})
            continue
        for run in selected:
            if run["status"] != "measured":
                rows.append({"scenario": scenario, "requested_policy": run["requested_policy"], "passed": False, "error": "Run did not complete successfully"})
                continue
            if len(run["outputs"]) != len(cpu["outputs"]):
                rows.append({"scenario": scenario, "requested_policy": run["requested_policy"], "passed": False, "error": "Output count differs"})
                continue
            same_model = bool(run.get("loaded_model", {}).get("sha256")) and run["loaded_model"]["sha256"] == cpu.get("loaded_model", {}).get("sha256")
            baseline_inputs = {str(Path(item["output"]).resolve()): str(Path(item["image"]).resolve())
                               for item in cpu.get("completed_stages", {}).get("image_outcomes", []) if item.get("ok") and item.get("output")}
            candidate_inputs = {str(Path(item["output"]).resolve()): str(Path(item["image"]).resolve())
                                for item in run.get("completed_stages", {}).get("image_outcomes", []) if item.get("ok") and item.get("output")}
            for index, (reference, candidate) in enumerate(zip(cpu["outputs"], run["outputs"])):
                reference_input = baseline_inputs.get(str(Path(reference["path"]).resolve()))
                candidate_input = candidate_inputs.get(str(Path(candidate["path"]).resolve()))
                same_input = bool(reference_input) and reference_input == candidate_input
                with Image.open(reference["path"]) as image:
                    left = np.asarray(image.convert("RGB"), dtype=np.int16)
                with Image.open(candidate["path"]) as image:
                    right = np.asarray(image.convert("RGB"), dtype=np.int16)
                row = {"scenario": scenario, "requested_policy": run["requested_policy"], "repetition": run["repetition"],
                       "output_index": index, "reference": reference, "candidate": candidate,
                       "reference_input":reference_input,"candidate_input":candidate_input,"same_input_identity":same_input,
                       "same_fitted_model_sha256":same_model,"fitted_model_sha256":run.get("loaded_model",{}).get("sha256"),
                       "actual_backends": run.get("actual_backends", {}), "same_dimensions": left.shape == right.shape}
                if left.shape == right.shape:
                    difference = np.abs(left - right)
                    maximum = int(difference.max())
                    row.update(max_abs_8bit_channel_difference=maximum,
                               mean_abs_8bit_channel_difference=float(difference.mean()),
                               changed_channel_fraction=float(np.count_nonzero(difference) / difference.size),
                               bit_identical_pixels=maximum == 0, within_one_8bit_step=maximum <= 1)
                    row["passed"] = maximum <= 1 and same_model and same_input
                else:
                    row["passed"] = False
                rows.append(row)
    result = {"passed": all(row["passed"] for row in rows), "comparisons": rows,
              "timed": False, "threshold": "one 8-bit quantization step for decoded PNGs",
              "limitations": ["This is not the 1e-5 floating-point identical-LUT arithmetic test.",
                              "Only applications of the shared fitted model are compared; independent detections/fits can differ.",
                              "CPU output is an equivalence reference, not independent physical color truth."]}
    (folder / "image-equivalence.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps({"passed": result["passed"], "comparisons": len(rows),
                      "maximum_8bit_channel_difference": max((row.get("max_abs_8bit_channel_difference", 0) for row in rows), default=None)}))
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
