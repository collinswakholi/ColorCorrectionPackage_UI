import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from colorcorrect import Engine, EngineError

ROOT = Path(__file__).resolve().parents[2]
FAKE = Path(__file__).with_name("fake_worker.py")
POPEN = subprocess.Popen


class DiscoveryTests(unittest.TestCase):
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
        self.mock.start()
        self.engine = Engine(sys.executable, root=ROOT, timeout=5)

    def tearDown(self):
        self.engine.close()
        self.mock.stop()

    def test_progress_and_paths(self):
        messages = []
        self.engine.progress = messages.append
        with self.engine as engine:
            reply = engine.detect(Path("a space/日本.jpg"))
            self.assertEqual(reply["echo"]["image"], str(Path("a space/日本.jpg")))
            self.assertEqual(reply["echo"]["policy"], "cpu")
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
                      (lambda: self.engine.fit("x", output="a", report="b", model_out="c"), "run"),
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


if __name__ == "__main__":
    unittest.main()
