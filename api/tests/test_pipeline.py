"""Familiar pipeline facade: lifecycle, argument mapping and explicit differences."""
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from colorcorrect import ColorCorrection, Config, CalibrationOptions, InvalidInputError


class PipelineTests(unittest.TestCase):
    def test_config_validates_before_worker_creation(self):
        for arguments in ({"save_path": ""}, {"save_path": "out", "do_gc": 1},
                          {"save_path": "out", "options": {}},
                          {"save_path": "out", "options": CalibrationOptions(model="linear", degree=0)}):
            with self.subTest(arguments=arguments), self.assertRaises(InvalidInputError):
                Config(**arguments)

    @patch("colorcorrect_pipeline.Client")
    def test_run_predict_reload_and_context_lifecycle(self, client):
        config = Config(save_path=Path("output"), do_gc=False, check_saturation=False,
                        options=CalibrationOptions(model="linear", degree=3))
        with ColorCorrection(config) as cc:
            model = cc.run("chart.jpg", White_Image="blank.jpg")
            args = client.return_value.calibrate.call_args
            self.assertEqual(args.args, ("chart.jpg",))
            self.assertEqual(args.kwargs["white"], "blank.jpg")
            self.assertEqual(args.kwargs["options"].model, "linear")
            self.assertEqual(args.kwargs["options"].degree, 3)
            self.assertFalse(args.kwargs["options"].do_gc)
            self.assertFalse(args.kwargs["options"].do_sat)
            self.assertIsNone(args.kwargs["options"].do_ffc)
            cc.predict_image("scene.jpg")
            model.apply.assert_called_with("scene.jpg", output_dir=Path("output"), options=None)
            cc.predict_images(["one.jpg", "two.jpg"], output_dir="batch", on_error="continue")
            model.apply.assert_called_with(["one.jpg", "two.jpg"], output_dir="batch", options=None, on_error="continue")
            cc.load_model("saved.json")
            self.assertIs(cc.model, client.return_value.load_calibration.return_value)
        client.return_value.close.assert_called_once()

    @patch("colorcorrect_pipeline.Client")
    def test_invalid_state_and_failed_fit_do_not_replace_model(self, client):
        cc = ColorCorrection()
        with self.assertRaises(InvalidInputError):
            cc.predict_image("x")
        with self.assertRaises(InvalidInputError):
            cc.run("x")
        original = cc.run("x", config=Config(save_path="first"))
        client.return_value.calibrate.side_effect = InvalidInputError("bad chart")
        with self.assertRaises(InvalidInputError):
            cc.run("x", config=Config(save_path="second"))
        self.assertIs(cc.model, original)
        self.assertEqual(cc.config.save_path, "first")

    def test_video_is_absent_from_public_api(self):
        from colorcorrect import CalibrationModel, Engine
        self.assertFalse(hasattr(CalibrationModel, "grade_video"))
        self.assertFalse(hasattr(Engine, "video"))
        self.assertFalse(hasattr(Engine, "video_grade"))
