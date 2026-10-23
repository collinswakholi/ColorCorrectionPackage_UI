"""Pure-stdlib aggregation checks; no runtime, NumPy, or worker is loaded."""
from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from scripts import summarize_compute as summary


class SummaryTests(unittest.TestCase):
    def selected(self, blocks=5, calls=20):
        return [(index, Path(f"lut-10-rust-cpu-block{index}.json"),
                 {"exit_code": 0, "runs": [{"ms": 2.0} for _ in range(calls)]})
                for index in range(blocks)]

    def test_complete_requires_every_expected_block_and_call(self):
        self.assertEqual("measured", summary.summarize_group(self.selected(), 5, 20)["status"])
        self.assertEqual("incomplete", summary.summarize_group(self.selected(4), 5, 20)["status"])
        wrong = self.selected()
        wrong[-1] = (5, wrong[-1][1], wrong[-1][2])
        self.assertEqual("incomplete", summary.summarize_group(wrong, 5, 20)["status"])
        self.assertEqual("incomplete", summary.summarize_group(self.selected(calls=19), 5, 20)["status"])

    def test_failed_teardown_retains_only_diagnostic_statistics(self):
        selected = self.selected()
        selected[-1][2]["exit_code"] = -1073740791
        result = summary.summarize_group(selected, 5, 20)
        self.assertEqual("failed", result["status"])
        self.assertEqual(100, result["n"])
        self.assertEqual(80, result["n_from_successful_processes"])
        self.assertEqual(2, result["median_ms"])
        self.assertTrue(result["statistics_are_diagnostic"])

    def test_invalid_timing_never_passes_or_emits_nan(self):
        selected = self.selected()
        selected[-1][2]["runs"][0]["ms"] = float("nan")
        result = summary.summarize_group(selected, 5, 20)
        self.assertEqual("failed", result["status"])
        self.assertEqual(99, result["n_valid_timings"])
        json.dumps(summary.finite_json(result), allow_nan=False)

    def test_override_is_explicit_for_first_run_design(self):
        result = summary.summarize_group(self.selected(calls=1), 5, 1)
        self.assertEqual("measured", result["status"])
        self.assertEqual(5, result["n"])

    def test_excluded_blocks_require_clean_replacements(self):
        temporary_root = Path(__file__).resolve().parents[2] / "_implementation" / "aggregation-tests"
        temporary_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temporary_root) as temporary:
            folder = Path(temporary).resolve()
            self.assertTrue(folder.is_relative_to(temporary_root.resolve()))
            original, replacement = folder / "original", folder / "replacement"
            original.mkdir()
            replacement.mkdir()
            for index, path, value in self.selected():
                (original / path.name).write_text(json.dumps(value), encoding="utf-8")
            (original / "exclusions.json").write_text(json.dumps({
                "exclude_from_final_comparisons": [{"mode": "lut", "size": 10,
                    "blocks": [0], "reason": "Concurrent workload"}]}), encoding="utf-8")
            destination = folder / "summary.json"
            def execute(extra):
                with patch("sys.argv", ["summarize_compute", "--input", str(original),
                                        *extra, "--output", str(destination)]), \
                        patch.dict("sys.modules", {"numpy": None}), redirect_stdout(io.StringIO()):
                    status = summary.main()
                return status, json.loads(destination.read_text(encoding="utf-8"))
            status, result = execute([])
            self.assertEqual(1, status)
            self.assertEqual("incomplete", result["summary"][0]["status"])
            self.assertEqual(1, len(result["excluded_files"]))
            self.assertIn("Concurrent workload", result["excluded_files"][0]["reason"])
            (replacement / "lut-10-rust-cpu-block0.json").write_text(json.dumps(self.selected()[0][2]), encoding="utf-8")
            status, result = execute(["--input", str(replacement)])
            self.assertEqual(0, status)
            self.assertTrue(result["complete"])
            self.assertIn(str(replacement / "lut-10-rust-cpu-block0.json"), result["summary"][0]["selected_files"])


if __name__ == "__main__":
    unittest.main()
