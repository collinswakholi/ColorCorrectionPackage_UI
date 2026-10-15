import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from colorcorrect import Engine, EngineError, SUPPORTED_COMMANDS, UNSUPPORTED_COMMANDS

ROOT = Path(__file__).resolve().parents[2]
FAKE = Path(__file__).with_name("fake_worker.py")
POPEN = subprocess.Popen


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

    def test_all_convenience_operations(self):
        operations = [(lambda: self.engine.warmup(), "warmup"),
                      (lambda: self.engine.fit("x", output="a", report="b", model_out="c"), "run"),
                      (lambda: self.engine.apply("m", "x", "out"), "apply"),
                      (lambda: self.engine.preview("m", "x", "cc", "out"), "preview_stage"),
                      (lambda: self.engine.export_cube("m", "out"), "export_cube")]
        for operation, expected in operations:
            self.assertEqual(operation()["echo"]["cmd"], expected)

    def test_retired_video_helpers_are_explicit_errors_without_starting_worker(self):
        for operation in (lambda: self.engine.video("missing.mp4", "unused.mp4"),
                          lambda: self.engine.video_grade("missing.json", "missing.mp4", "unused.mp4")):
            with self.assertRaises(EngineError) as caught:
                operation()
            reply = caught.exception.reply
            self.assertFalse(reply["ok"])
            self.assertEqual(reply["code"], "unsupported_feature")
            self.assertEqual(reply["feature"], "video")
            self.assertIn("still images only", str(caught.exception))
        self.launch.assert_not_called()
        self.assertIsNone(self.engine._process)
        self.assertFalse(set(SUPPORTED_COMMANDS) & set(UNSUPPORTED_COMMANDS))

    def test_retired_video_requests_ignore_old_options_and_support_unchecked_error(self):
        for command in ("video", "video_grade", "VIDEO", "VIDEO_GRADE"):
            reply = self.engine.request(command, do_ffc=True, nn={"hidden_layers": []}, check=False)
            self.assertFalse(reply["ok"])
            self.assertEqual(reply["code"], "unsupported_feature")
        self.launch.assert_not_called()
        # The compatibility error must not poison subsequent still requests.
        self.assertTrue(self.engine.devices()["ok"])

    def test_environment_does_not_discover_or_add_video_sidecars(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "refs").mkdir()
            (root / "refs/neutrals.json").write_text("{}", encoding="utf-8")
            (root / "models/opt2").mkdir(parents=True)
            tools = root / "tools/ffmpeg"
            tools.mkdir(parents=True)
            for filename in ("ffmpeg", "ffmpeg.exe", "ffprobe", "ffprobe.exe"):
                (tools / filename).touch()
            with patch.dict("os.environ", {}, clear=True), patch("colorcorrect.Path.home", return_value=root):
                engine = Engine(sys.executable, root=root)
                env = engine._environment()
            self.assertNotIn("CCP_FFMPEG", env)
            self.assertNotIn("CCP_FFPROBE", env)
            self.assertNotIn(str(tools), env["PATH"].split(os.pathsep))

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
