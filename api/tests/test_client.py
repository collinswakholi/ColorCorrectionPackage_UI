import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from colorcorrect import Engine, EngineError, Client, MissingRuntimeError

ROOT = Path(__file__).resolve().parents[2]
FAKE = Path(__file__).with_name("fake_worker.py")
POPEN = subprocess.Popen


class DiscoveryTests(unittest.TestCase):
    def test_missing_native_core_runtime_fails_before_launch_and_does_not_borrow_ambient_runtime(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "refs").mkdir(); (root / "refs/neutrals.json").write_text("{}")
            worker = root / ("ccp-engine.exe" if os.name == "nt" else "ccp-engine")
            worker.touch()
            runtime_name = "onnxruntime.dll" if os.name == "nt" else ("libonnxruntime.dylib" if sys.platform == "darwin" else "libonnxruntime.so")
            missing = root / runtime_name
            ambient = root / "developer-runtime"
            ambient.touch()
            with patch.dict(os.environ, {"ORT_DYLIB_PATH": str(ambient)}), patch("colorcorrect.subprocess.Popen") as launch:
                with self.assertRaisesRegex(FileNotFoundError, "Required ONNX Runtime library is missing") as error:
                    Engine(root=root).devices()
                # Worker discovery canonicalizes Windows short-name/temp aliases.
                self.assertIn(str(missing.resolve()), str(error.exception))
                with self.assertRaisesRegex(MissingRuntimeError, "Required ONNX Runtime library is missing"):
                    with Client(root=root):
                        self.fail("Missing native runtime must be reported before entering the client context")
            launch.assert_not_called()

    def test_optional_gpu_plugin_is_not_a_cpu_launch_prerequisite(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "refs").mkdir(); (root / "refs/neutrals.json").write_text("{}")
            worker = root / ("ccp-engine.exe" if os.name == "nt" else "ccp-engine")
            worker.touch()
            runtime_name = "onnxruntime.dll" if os.name == "nt" else ("libonnxruntime.dylib" if sys.platform == "darwin" else "libonnxruntime.so")
            (root / runtime_name).touch()
            def launch(args, **options):
                return POPEN([sys.executable, str(FAKE)], **options)
            with patch("colorcorrect.subprocess.Popen", side_effect=launch):
                with Client(root=root) as client:
                    self.assertTrue(client.engine.devices()["ok"])
            self.assertFalse(any(root.glob("*providers_webgpu*")))

    def test_explicit_package_root_ignores_ambient_worker_and_runtime(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "refs").mkdir()
            (root / "refs/neutrals.json").write_text("{}")
            worker = root / ("ccp-engine.exe" if os.name == "nt" else "ccp-engine")
            worker.touch()
            runtime = root / ("onnxruntime.dll" if os.name == "nt" else
                              "libonnxruntime.dylib" if sys.platform == "darwin" else "libonnxruntime.so")
            runtime.touch()
            with patch.dict(os.environ, {"CCP_ENGINE_EXE": str(root / "developer-worker"),
                                         "ORT_DYLIB_PATH": str(root / "developer-runtime")}):
                engine = Engine(root=root)
                self.assertEqual(engine.executable, worker.resolve())
                self.assertEqual(engine._environment()["ORT_DYLIB_PATH"], str(runtime.resolve()))
                runtime.unlink()
                self.assertEqual(engine._environment()["ORT_DYLIB_PATH"], str(runtime.resolve()))
                worker.unlink()
                with self.assertRaises(FileNotFoundError):
                    Engine(root=root)

    def test_linux_package_wrapper_preferred_and_explicit_override_preserved(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "refs").mkdir()
            (root / "refs/neutrals.json").write_text("{}")
            for name in ("ccp-engine", "ccp-engine-launch"):
                (root / name).touch()
            with patch("colorcorrect.sys.platform", "linux"):
                self.assertEqual(Engine(root=root).executable, (root / "ccp-engine-launch").resolve())
                self.assertEqual(Engine(root / "ccp-engine", root=root).executable, (root / "ccp-engine").resolve())


class ClientTests(unittest.TestCase):
    def setUp(self):
        def launch(args, **options):
            return POPEN([sys.executable, str(FAKE)], **options)
        self.mock = patch("colorcorrect.subprocess.Popen", side_effect=launch)
        self.launch = self.mock.start()
        self.engine = Engine(sys.executable, root=ROOT, timeout=5)

    def tearDown(self):
        self.engine.close()
        self.mock.stop()

    def test_explicit_cpu_override_reaches_worker(self):
        with Engine(sys.executable, root=ROOT, policy="cpu") as cpu:
            self.assertEqual(cpu.devices()["echo"]["policy"], "cpu")

    def test_random_sampling_requires_a_capable_worker(self):
        reply = self.engine.request("run", check=False, samples_per_patch=20, sampling_seed=42)
        self.assertFalse(reply["ok"])
        self.assertEqual(reply["code"], "unsupported_capability")
        with self.assertRaisesRegex(EngineError, "updated worker") as caught:
            self.engine.fit("chart.png", output="out.png", report="report.json",
                            model_out="model.json", samples_per_patch=20, sampling_seed=42)
        self.assertEqual(caught.exception.reply, reply)
        self.assertTrue(self.engine.devices()["ok"])

    def test_random_sampling_rejects_wrong_or_unversioned_capability(self):
        for marker in (None, True, {}, "random", "random-lcg32-v2"):
            with self.subTest(marker=marker), patch.dict(os.environ,
                    {"FAKE_WORKER_PATCH_SAMPLING": json.dumps(marker)}):
                try:
                    with self.assertRaisesRegex(EngineError, "updated worker"):
                        self.engine.fit("chart.png", output="out.png", report="report.json",
                                        model_out="model.json", samples_per_patch=1)
                    self.assertEqual(self.engine.devices()["echo"]["cmd"], "devices")
                finally:
                    self.engine.close()

    def test_random_sampling_counts_and_seed_are_not_rewritten(self):
        with patch.dict(os.environ, {"FAKE_WORKER_PATCH_SAMPLING": '"random-lcg32-v1"'}):
            for count in (1, 10, 20, 50):
                with self.subTest(count=count):
                    echo = self.engine.fit("chart.png", output="out.png", report="report.json",
                                           model_out="model.json", samples_per_patch=count,
                                           sampling_seed=42)["echo"]
                    self.assertEqual(echo["samples_per_patch"], count)
                    self.assertEqual(echo["sampling_seed"], 42)
                    self.assertNotIn("fit_samples", echo)
            for seed in (0, 0xFFFFFFFF):
                echo = self.engine.request("run", samples_per_patch=1, sampling_seed=seed)["echo"]
                self.assertEqual(echo["sampling_seed"], seed)

    def test_grid_default_and_explicit_grid_need_no_random_capability(self):
        default = self.engine.request("run")["echo"]
        self.assertNotIn("fit_samples", default)
        self.assertNotIn("samples_per_patch", default)
        for axis in (1, 9, 200):
            with self.subTest(axis=axis):
                self.assertEqual(self.engine.request("run", fit_samples=axis)["echo"]["fit_samples"], axis)

    def test_invalid_sampling_is_rejected_before_worker_start(self):
        invalid = [{"fit_samples": value} for value in (None, True, False, "9", 1.5, 0, -1, 201, 2**32 + 9)]
        invalid += [{"samples_per_patch": value} for value in (None, True, "10", 1.5, 0, -1, 4097, 2**64)]
        invalid += [{"samples_per_patch": 1, "sampling_seed": value}
                    for value in (None, True, "42", 1.5, -1, 2**32)]
        invalid += [{"sampling_seed": 42}, {"fit_samples": 9, "samples_per_patch": 10},
                    {"n_samples": 10}, {"random_seed": 42}]
        for options in invalid:
            with self.subTest(options=options), self.assertRaises(ValueError):
                self.engine.request("run", **options)
        self.launch.assert_not_called()

    def test_calibrate_validates_sampling_before_detection_or_creating_results(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory) / "not-created"
            with patch.object(self.engine, "detect") as detect:
                with self.assertRaisesRegex(ValueError, "fit_samples"):
                    self.engine.calibrate("chart.png", folder=folder, fit_samples="9")
                detect.assert_not_called()
            self.assertFalse(folder.exists())
        self.launch.assert_not_called()

    def test_apply_rejects_unknown_formats_before_worker_start(self):
        for output_format in (None, "tiff", "webp", "", True, ["png"]):
            with self.subTest(output_format=output_format), self.assertRaises(ValueError):
                self.engine.apply("model.json", "image.jpg", "out", output_format=output_format)
        self.launch.assert_not_called()
        for output_format in ("png", "jpg", "jpeg", "PNG", "JPEG"):
            self.assertEqual(self.engine.apply("m", "i", "out", output_format=output_format)
                             ["echo"]["output_format"], output_format)

    def test_written_fit_outputs_require_supported_extensions(self):
        for output in ("out", "out.tiff", "out.webp", "out.bmp"):
            with self.subTest(output=output), self.assertRaisesRegex(ValueError, "still output"):
                self.engine.fit("chart.png", output=output, report="report.json", model_out="model.json")
        self.launch.assert_not_called()
        # A report/model-only run does not serialize its dummy final destination.
        echo = self.engine.request("run", output="unused.bin", write_output=False)["echo"]
        self.assertFalse(echo["write_output"])
        self.assertEqual(echo["output"], "unused.bin")

    def test_disabled_ffc_does_not_validate_or_load_inactive_settings(self):
        echo = self.engine.request("run", do_ffc=False, white="missing.png",
                                   ffc={"method": "unsupported-inactive-method"})["echo"]
        self.assertFalse(echo["do_ffc"])
        self.assertEqual(echo["white"], "missing.png")

    def test_progress_and_paths(self):
        messages = []
        self.engine.progress = messages.append
        with self.engine as engine:
            reply = engine.detect(Path("a space/日本.jpg"))
            self.assertEqual(reply["echo"]["image"], str(Path("a space/日本.jpg")))
            self.assertEqual(reply["echo"]["policy"], "auto")
            self.assertEqual(messages, ["working"])
        self.assertIsNone(self.engine._process)

    def test_stderr_flood_does_not_deadlock(self):
        self.assertTrue(self.engine.request("flood")["ok"])
        self.assertLessEqual(len(self.engine._stderr), 80)

    def test_timeout_restarts_without_stale_reply(self):
        self.engine.devices()
        with self.assertRaises(TimeoutError):
            self.engine.request("slow", timeout=.03)
        self.assertIsNone(self.engine._process)
        self.assertEqual(self.engine.devices()["echo"]["cmd"], "devices")

    def test_protocol_failure_and_crash_recover(self):
        for command in ("invalid", "crash"):
            with self.assertRaises(EngineError):
                self.engine.request(command)
            self.assertTrue(self.engine.devices()["ok"])

    def test_error_and_partial_batch_are_not_silent(self):
        for command in ("fail", "partial"):
            with self.assertRaises(EngineError) as caught:
                self.engine.request(command)
            self.assertIn("error", caught.exception.reply)
        self.assertTrue(self.engine.request("partial", check=False)["ok"])

    def test_ffc_requires_white_and_nn_integer_normalizes(self):
        with self.assertRaises(ValueError):
            self.engine.request("run", do_ffc=True)
        reply = self.engine.request("run", do_ffc=True, white=Path("Blank.JPG"), nn={"hidden_layers": 30})
        self.assertEqual(reply["echo"]["nn"]["hidden_layers"], [30])

    def test_invalid_json_values_and_limits(self):
        with self.assertRaises(ValueError):
            self.engine.request("run", tol=float("nan"))
        with self.assertRaises(ValueError):
            self.engine.export_cube("model.json", "x.cube", size=10000)
        with self.assertRaises(ValueError):
            self.engine.request("devices", timeout=0)

    def test_nn_configuration_rejects_silent_fallbacks(self):
        for nn in ({"hidden_layers": []}, {"hidden_layers": True}, {"hidden_layers": [0, 32]},
                   {"hidden_layers": [64] * 5}, {"optimizer": "typo"}, {"dropout": 1},
                   {"dropout": "0.1"}, {"batch_norm": "false"}):
            with self.subTest(nn=nn), self.assertRaises(ValueError):
                self.engine.request("run", nn=nn)

    def test_old_worker_cannot_silently_ignore_custom_references(self):
        with self.assertRaises(EngineError) as caught:
            self.engine.request("run", reference_profiles=[{"id": "new-profile"}])
        self.assertEqual(caught.exception.reply["code"], "unsupported_capability")
        self.assertFalse(self.engine.request("run", check=False, reference_assignments=[{}])["ok"])
        self.assertTrue(self.engine.devices()["ok"])

    def test_all_convenience_operations(self):
        operations = [(lambda: self.engine.warmup(), "warmup"),
                      (lambda: self.engine.fit("x", output="a.png", report="b", model_out="c"), "run"),
                      (lambda: self.engine.apply("m", "x", "out"), "apply"),
                      (lambda: self.engine.preview("m", "x", "cc", "out"), "preview_stage"),
                      (lambda: self.engine.export_cube("m", "out"), "export_cube")]
        for operation, expected in operations:
            self.assertEqual(operation()["echo"]["cmd"], expected)

    def test_calibrate_preserves_options_and_reuses_ffc(self):
        with tempfile.TemporaryDirectory() as directory:
            result = self.engine.calibrate("chart.jpg", folder=directory, handshake={"test": True},
                white="Blank.JPG", ffc={"method": "pls", "degree": 5}, crop_rect="0,0,100,100",
                model="nn", nn={"hidden_layers": [64, 32]}, degree=3,
                output="custom.png", model_out=Path(directory) / "custom.json")
            echo = result.report["echo"]
            self.assertEqual(echo["nn"]["hidden_layers"], [64, 32])
            self.assertEqual(echo["degree"], 3)
            self.assertEqual(echo["output"], "custom.png")
            applied = result.apply("scene.jpg")["echo"]
            self.assertTrue(applied["do_ffc"])
            self.assertEqual(applied["white"], "Blank.JPG")
            self.assertEqual(applied["ffc"], {"method": "pls", "degree": 5})
            self.assertEqual(applied["crop_rect"], "0,0,100,100")
            self.assertEqual(applied["model"], str(result.model))
            self.assertFalse(result.apply("scene.jpg", do_ffc=False)["echo"]["do_ffc"])

    def test_calibrate_detects_once_and_uses_unique_folders(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(self.engine, "detect", return_value={"handshake": {"chart": "classic"}}) as detect:
                first = self.engine.calibrate("chart.jpg", folder=directory, detect_options={"crop_inset": .2})
                detect.assert_called_once_with("chart.jpg", crop_inset=.2)
            second = self.engine.calibrate("chart.jpg", folder=directory, handshake={})
            self.assertNotEqual(first.folder, second.folder)
            self.assertEqual(first.report["echo"]["handshake"], {"chart": "classic"})

    def test_callback_error_does_not_poison_stream(self):
        def bad_callback(_):
            raise RuntimeError("callback failed")
        self.engine.progress = bad_callback
        with self.assertRaises(RuntimeError):
            self.engine.devices()
        self.engine.progress = None
        self.assertTrue(self.engine.devices()["ok"])

    def test_close_requests_deferred_artifact_flush(self):
        with tempfile.TemporaryDirectory(prefix="colorcorrect-client-") as directory:
            marker = Path(directory) / "preview.txt"
            with self.engine as engine:
                engine.request("deferred", path=marker)
                self.assertFalse(marker.exists())
            self.assertEqual(marker.read_text(encoding="utf-8"), "complete")

    def test_shutdown_acknowledgement_allows_native_cleanup_before_exit(self):
        with tempfile.TemporaryDirectory(prefix="colorcorrect-cleanup-") as directory:
            for command in ("close", "shutdown", "quit"):
                with self.subTest(command=command):
                    marker = Path(directory) / f"{command}.txt"
                    self.engine.request("deferred_after_ack", path=marker)
                    process = self.engine._process
                    if command == "close":
                        self.engine.close()
                    else:
                        self.engine.request(command)
                    self.assertEqual(process.returncode, 0)
                    self.assertEqual(marker.read_text(encoding="utf-8"), "complete")
                    self.assertIsNone(self.engine._process)

    def test_unresponsive_cleanup_after_acknowledgement_is_still_terminated(self):
        with tempfile.TemporaryDirectory(prefix="colorcorrect-hung-cleanup-") as directory:
            marker = Path(directory) / "unfinished.txt"
            self.engine.request("deferred_after_ack", path=marker, delay=60)
            process = self.engine._process
            self.engine.timeout = .1
            self.engine.close()
            self.assertIsNotNone(process.poll())
            self.assertNotEqual(process.returncode, 0)
            self.assertFalse(marker.exists())

    def test_explicit_shutdown_reports_nonzero_native_cleanup_exit(self):
        with tempfile.TemporaryDirectory(prefix="colorcorrect-failed-cleanup-") as directory:
            self.engine.request("deferred_after_ack", path=Path(directory) / "partial.txt", exit_code=3)
            with self.assertRaisesRegex(EngineError, "cleanup failed") as caught:
                self.engine.request("shutdown")
            self.assertEqual(caught.exception.reply["exit_code"], 3)
            self.assertIsNone(self.engine._process)

    def test_context_cleanup_does_not_mask_body_error(self):
        with tempfile.TemporaryDirectory(prefix="colorcorrect-body-error-") as directory:
            with self.assertRaisesRegex(ValueError, "original error"):
                with self.engine:
                    self.engine.request("deferred_after_ack", path=Path(directory) / "partial.txt", exit_code=3)
                    raise ValueError("original error")
            self.assertIsNone(self.engine._process)

    def test_keyboard_interrupt_during_request_still_stops_worker(self):
        def interrupt(_):
            raise KeyboardInterrupt()
        self.engine.progress = interrupt
        with self.assertRaises(KeyboardInterrupt):
            self.engine.devices()
        self.assertIsNone(self.engine._process)
        self.engine.progress = None
        self.assertTrue(self.engine.devices()["ok"])

    def test_interrupt_after_shutdown_acknowledgement_still_reaps_owned_worker(self):
        with tempfile.TemporaryDirectory(prefix="colorcorrect-interrupted-cleanup-") as directory:
            for command in ("close", "shutdown", "quit"):
                with self.subTest(command=command):
                    marker = Path(directory) / f"{command}.txt"
                    self.engine.request("deferred_after_ack", path=marker, delay=60)
                    process = self.engine._process
                    wait = process.wait
                    interrupted = False
                    def interrupt_first_wait(*args, **kwargs):
                        nonlocal interrupted
                        if not interrupted:
                            interrupted = True
                            raise KeyboardInterrupt()
                        return wait(*args, **kwargs)
                    with patch.object(process, "wait", side_effect=interrupt_first_wait):
                        with self.assertRaises(KeyboardInterrupt):
                            if command == "close":
                                self.engine.close()
                            else:
                                self.engine.request(command)
                    self.assertTrue(interrupted)
                    self.assertIsNotNone(process.poll())
                    self.assertFalse(marker.exists())
                    self.assertIsNone(self.engine._process)


if __name__ == "__main__":
    unittest.main()
