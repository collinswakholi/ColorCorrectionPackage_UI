"""Merge clean benchmark blocks; later inputs replace matching earlier blocks.

Example: --input main-run --input clean-1080 --input clean-4k-block0
Use --nvidia-input for an additional DirectML adapter-1 run.
Passing rows require five blocks of twenty calls unless explicitly overridden.
Input-directory exclusion rules apply before replacements are selected. Settings
and asset fingerprints remain in source manifests for a separate compatibility audit.
"""
from __future__ import annotations
import argparse
from collections import Counter, defaultdict
import json
import math
from pathlib import Path
import re
import statistics
import sys


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def exclusion_reason(rules: list[dict], mode: str, size: int, backend: str, block: int) -> str | None:
    for rule in rules:
        blocks = rule["blocks"]
        if (rule["mode"] == mode and rule["size"] == size
                and (blocks == "all" or block in blocks)
                and (not rule.get("backend") or rule["backend"] == backend)):
            return str(rule.get("reason") or "Source exclusion rule")
    return None


def summarize_group(selected: list, expected_blocks: int, expected_calls: int) -> dict:
    selected = sorted(selected)
    all_calls, valid_times, successful_calls, invalid_times = 0, [], 0, []
    block_checks = []
    for index, path, result in selected:
        values = result.get("runs", [])
        valid_list = isinstance(values, list)
        values = values if valid_list else []
        all_calls += len(values)
        if result.get("exit_code") == 0:
            successful_calls += len(values)
        times = []
        for run_index, run in enumerate(values):
            value = run.get("ms") if isinstance(run, dict) else None
            if (isinstance(value, (int, float)) and not isinstance(value, bool)
                    and math.isfinite(value) and value >= 0):
                times.append(float(value))
            else:
                invalid_times.append({"file": str(path), "run_index": run_index,
                                      "value": repr(value), "reason": "Expected finite nonnegative timing"})
        if not valid_list:
            invalid_times.append({"file": str(path), "reason": "runs is not an array"})
        valid_times.extend(times)
        block_checks.append({"block": index, "file": str(path), "exit_code": result.get("exit_code"),
                             "emitted_calls": len(values), "valid_timing_calls": len(times),
                             "exact_call_count": len(values) == expected_calls,
                             "median_ms": statistics.median(times) if times else None})
    expected_indices = list(range(expected_blocks))
    actual_indices = [index for index, _, _ in selected]
    checks = {"exact_block_indices": actual_indices == expected_indices,
              "each_process_exact_call_count": all(row["exact_call_count"] for row in block_checks),
              "every_process_exit_zero": bool(selected) and all(row["exit_code"] == 0 for row in block_checks),
              "all_call_times_finite_nonnegative": not invalid_times}
    status = ("failed" if not checks["every_process_exit_zero"] or invalid_times else
              "measured" if all(checks.values()) else "incomplete")
    ordered = sorted(valid_times)
    errors = [result.get("max_abs_rgb_error") for _, _, result in selected]
    errors = [value for value in errors if isinstance(value, (int, float)) and math.isfinite(value)]
    return {"n": all_calls, "n_valid_timings": len(valid_times), "n_from_successful_processes": successful_calls,
            "failed_processes": [{"file": row["file"], "exit_code": row["exit_code"], "emitted_calls": row["emitted_calls"]}
                                 for row in block_checks if row["exit_code"] != 0],
            "blocks": len(selected), "status": status,
            "expected_blocks": expected_blocks, "expected_calls_per_process": expected_calls,
            "expected_block_indices": expected_indices, "actual_block_indices": actual_indices,
            "completeness_checks": checks, "per_process_checks": block_checks,
            "invalid_timings": invalid_times, "statistics_are_diagnostic": status != "measured",
            "median_ms": statistics.median(valid_times) if valid_times else None,
            "p95_ms": ordered[math.ceil(.95 * len(ordered)) - 1] if ordered else None,
            "block_medians_ms": [row["median_ms"] for row in block_checks],
            "max_abs_rgb_error": max(errors, default=None),
            "cold_init_ms": [result.get("cold_init_ms") for _, _, result in selected],
            "cold_first_ms": [result.get("cold_first_ms") for _, _, result in selected],
            "peak_harness_working_set_bytes": max((result.get("peak_working_set_bytes") or 0 for _, _, result in selected), default=None),
            "selected_files": [str(path) for _, path, _ in selected]}


def finite_json(value):
    """Keep invalid raw values in source files; emit strict JSON in this summary."""
    if isinstance(value, float) and not math.isfinite(value):
        return None
    if isinstance(value, dict):
        return {key: finite_json(item) for key, item in value.items()}
    if isinstance(value, list):
        return [finite_json(item) for item in value]
    return value


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, action="append", default=[])
    parser.add_argument("--nvidia-input", type=Path, action="append", default=[])
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--expected-blocks", type=int, default=5)
    parser.add_argument("--expected-calls", "--expected-repetitions", type=int, default=20,
                        help="Exact measured calls per subprocess (not warmups or cold_first_ms)")
    args = parser.parse_args()
    if args.expected_blocks < 1 or args.expected_calls < 1:
        parser.error("Expected blocks and calls must be positive")
    blocks = {}
    profiles = []
    sources, excluded, replacements, source_errors = [], [], [], []
    for directory, variant in [(p, "") for p in args.input] + [(p, "-nvidia") for p in args.nvidia_input]:
        manifest_path, exclusions_path = directory / "manifest.json", directory / "exclusions.json"
        revisions_path = directory / "binary-revisions.json"
        source = {"directory": str(directory), "variant": variant,
                  "manifest": {"path": str(manifest_path), "exists": manifest_path.is_file()},
                  "exclusions": {"path": str(exclusions_path), "exists": exclusions_path.is_file()},
                  "binary_revisions": {"path": str(revisions_path), "exists": revisions_path.is_file()},
                  "settings_fingerprints_verified": False,
                  "fingerprint_audit": "Inspect source manifest assets/settings; compatible instrumentation revisions are allowed."}
        sources.append(source)
        rules = []
        if not directory.is_dir():
            source_errors.append({"path": str(directory), "error": "Input directory does not exist"})
            continue
        try:
            if manifest_path.is_file():
                source["manifest"]["value"] = read_json(manifest_path)
                source["manifest"]["parsed"] = True
            if revisions_path.is_file():
                source["binary_revisions"].update(value=read_json(revisions_path), parsed=True)
            if exclusions_path.is_file():
                exclusions = read_json(exclusions_path)
                rules = exclusions.get("exclude_from_final_comparisons", [])
                if not isinstance(rules, list) or any(not isinstance(rule, dict)
                    or not isinstance(rule.get("mode"), str) or not isinstance(rule.get("size"), int)
                    or not (rule.get("blocks") == "all" or isinstance(rule.get("blocks"), list)
                            and all(isinstance(index, int) and index >= 0 for index in rule["blocks"])) for rule in rules):
                    raise ValueError("Invalid mode/size/blocks exclusion rules")
                source["exclusions"].update(parsed=True, value=exclusions)
        except (OSError, ValueError, UnicodeError, AttributeError) as error:
            source_errors.append({"path": str(directory), "error": f"Cannot safely read source controls: {error}"})
            continue
        for path in sorted(directory.glob("*.json")):
            try:
                value = read_json(path)
            except (OSError, ValueError, UnicodeError) as error:
                if "-block" in path.stem:
                    source_errors.append({"path": str(path), "error": str(error)})
                continue
            if isinstance(value, list):
                events = [event for event in value if isinstance(event, dict) and event.get("cat") == "Node"]
                if events:
                    counts = Counter(event.get("args", {}).get("provider", "unknown") for event in events)
                    unique = defaultdict(set)
                    for event in events:
                        provider = event.get("args", {}).get("provider", "unknown")
                        unique[provider].add((event.get("name"), event.get("args", {}).get("op_name")))
                    profiles.append({"file": str(path), "provider_events": dict(counts),
                                     "unique_profiled_operations": {k: len(v) for k, v in unique.items()}})
                continue
            if not isinstance(value, dict) or value.get("profiling_enabled") or "-block" not in path.stem:
                continue
            match = re.fullmatch(r"(lut|detect|plane)-(\d+)-(.+)-block(\d+)", path.stem)
            if match is None:
                source_errors.append({"path": str(path), "error": "Unrecognized benchmark block filename"})
                continue
            file_mode, file_size, file_backend, file_block = match.groups()
            backend = value.get("backend") or file_backend
            mode = value.get("mode") or file_mode
            size = value.get("size") or value.get("pixels") or int(file_size)
            if backend == "wgpu":
                backend = f"wgpu-{value['adapter_index']}" if "adapter_index" in value else file_backend
            if (mode, size, backend) != (file_mode, int(file_size), file_backend):
                source_errors.append({"path": str(path), "error": "Block metadata conflicts with filename"})
                continue
            block_index = int(file_block)
            reason = exclusion_reason(rules, mode, size, backend, block_index)
            if reason:
                excluded.append({"file": str(path), "reason": reason, "exclusions": str(exclusions_path),
                                 "mode": mode, "size": size, "backend": backend + variant, "block": block_index})
                continue
            key = (mode, size, backend + variant, block_index)
            if key in blocks:
                replacements.append({"replaced_file": str(blocks[key][0]), "selected_file": str(path),
                                     "mode": mode, "size": size, "backend": backend + variant, "block": block_index})
            blocks[key] = (path, value)
    grouped = defaultdict(list)
    for (mode, size, backend, block_index), item in blocks.items():
        grouped[(mode, size, backend)].append((block_index, *item))
    summaries = []
    inference_comparisons = []
    for (mode, size, backend), selected in sorted(grouped.items()):
        summaries.append({"mode": mode, "size": size, "backend": backend,
                          **summarize_group(selected, args.expected_blocks, args.expected_calls)})
    try:
        import numpy as np
    except ImportError:
        inference_comparisons.append({"status": "not_measured", "reason": "numpy unavailable"})
    else:
        for (mode, size, backend), selected in sorted(grouped.items()):
            if mode not in ("detect", "plane") or backend in ("onnx-cpu", "onnx-cpu-nvidia"):
                continue
            baseline_backend = "onnx-cpu-nvidia" if backend.endswith("-nvidia") else "onnx-cpu"
            baseline = grouped.get((mode, size, baseline_backend), [])
            baseline = next(((path, result) for _, path, result in sorted(baseline)
                             if result.get("exit_code") == 0 and path.with_suffix(".bin").is_file()), None)
            candidate = next(((path, result) for _, path, result in sorted(selected)
                              if result.get("exit_code") == 0 and path.with_suffix(".bin").is_file()), None)
            if baseline is None or candidate is None:
                continue
            reference = np.fromfile(baseline[0].with_suffix(".bin"), dtype="<f4")
            actual = np.fromfile(candidate[0].with_suffix(".bin"), dtype="<f4")
            same_shape = baseline[1].get("output_dims") == candidate[1].get("output_dims") and reference.shape == actual.shape
            row = {"mode": mode, "size": size, "backend": backend, "identical_shape": same_shape,
                   "reference_file": str(baseline[0]), "candidate_file": str(candidate[0]),
                   "basis": "first forward tensor vs ONNX CPU FP32; mixed coordinate/confidence channels, not RGB color quality"}
            if same_shape:
                difference = np.abs(reference.astype(np.float64) - actual.astype(np.float64))
                row.update(max_absolute_tensor_difference=float(difference.max(initial=0)),
                           mean_absolute_tensor_difference=float(difference.mean()),
                           finite=bool(np.isfinite(reference).all() and np.isfinite(actual).all()))
            inference_comparisons.append(row)
    complete = bool(summaries) and not source_errors and all(row["status"] == "measured" for row in summaries)
    report = {"command": sys.argv, "complete": complete, "summary": summaries, "profiles": profiles,
              "expected_blocks": args.expected_blocks, "expected_calls_per_process": args.expected_calls,
              "source_controls": sources, "source_errors": source_errors, "excluded_files": excluded,
              "replaced_blocks": replacements, "inference_tensor_comparisons": inference_comparisons,
              "limitations": ["Completeness is checked for discovered groups; this is not a required-workload matrix validator.",
                              "Source settings/model/input/runtime fingerprints require a separate compatibility audit.",
                              "Failed/incomplete row medians are diagnostic; they do not certify passing performance.",
                              "Backend labels alone are not node-provider or physical-adapter proof."]}
    args.output.write_text(json.dumps(finite_json(report), indent=2, allow_nan=False), encoding="utf-8")
    print(args.output)
    return 0 if complete else 1


if __name__ == "__main__":
    raise SystemExit(main())
