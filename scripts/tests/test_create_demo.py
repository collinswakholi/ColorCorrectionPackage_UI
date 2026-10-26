"""Media-tool regression checks; synthetic colors test encoding, not application UI."""
from pathlib import Path
import importlib.util
import json
import tempfile
import unittest

try:
    import PIL
except ImportError:
    PIL = None


@unittest.skipUnless(PIL, "Pillow development-only media tooling is not installed")
class DemoEncoderTests(unittest.TestCase):
    def setUp(self):
        from PIL import Image
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        for name, color in (("one", (12, 34, 56)), ("two", (200, 130, 70))):
            Image.new("RGB", (12, 8), color).save(self.root/f"{name}.png")
        spec = importlib.util.spec_from_file_location("create_demo", Path(__file__).resolve().parents[1]/"create_demo.py")
        self.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.module)
        self.story = {"source_color_space": "srgb", "frames": [
            {"id": "one", "source": "one.png", "caption": "One", "duration_ms": 25000},
            {"id": "two", "source": "two.png", "caption": "Two", "duration_ms": 25000, "comparison": True}]}
        self.path = self.root/"story.json"

    def tearDown(self):
        self.temporary.cleanup()

    def write(self):
        self.path.write_text(json.dumps(self.story), encoding="utf-8")

    def test_animation_round_trip_preserves_source_pixels_timing_and_gallery(self):
        self.write()
        out = self.root/"output"
        report = self.module.create_demo(self.path, out)
        self.assertTrue(report["apng_hold_pixels_exact"])
        self.assertEqual(report["total_duration_ms"], 50000)
        self.assertEqual(report["dimensions"], [12, 8])
        self.assertTrue(all(s["gif_pixel_difference"]["max_absolute_channel_error_8bit"] == 0 for s in report["frames"]))
        with self.assertRaises(FileExistsError):
            self.module.create_demo(self.path, out)

    def test_identical_adjacent_captures_can_merge_without_losing_hold_verification(self):
        self.story["frames"][0]["duration_ms"] = 15000
        self.story["frames"].insert(1, {"id": "same", "source": "one.png", "duration_ms": 15000})
        self.story["frames"][2]["duration_ms"] = 20000
        self.write()
        report = self.module.create_demo(self.path, self.root/"output")
        self.assertTrue(report["apng_hold_pixels_exact"])
        self.assertEqual(report["total_duration_ms"], 50000)

    def test_a_single_repeated_still_is_not_claimed_as_an_animation(self):
        self.story["frames"][1]["source"] = "one.png"
        self.write()
        with self.assertRaisesRegex(ValueError, "distinct actual captures"):
            self.module.create_demo(self.path, self.root/"output")

    def test_scientific_comparisons_reject_crossfades_before_writing(self):
        self.story["frames"][1]["transition_ms"] = 160
        self.write()
        with self.assertRaisesRegex(ValueError, "Scientific comparison"):
            self.module.create_demo(self.path, self.root/"output")
        self.assertFalse((self.root/"output").exists())

    def test_mismatched_window_sizes_require_recapture(self):
        from PIL import Image
        Image.new("RGB", (5, 5)).save(self.root/"two.png")
        self.write()
        with self.assertRaisesRegex(ValueError, "same app-window size"):
            self.module.load_storyboard(self.path)

    def test_short_ordinary_transition_retains_exact_holds_and_requested_timing(self):
        self.story["frames"][1].update(comparison=False, transition_ms=160,
                                     quality_regions={"center": [2, 2, 8, 6]})
        self.write()
        report = self.module.create_demo(self.path, self.root/"output")
        self.assertEqual(report["total_duration_ms"], 50160)
        self.assertGreater(report["apng_frames"], 2)
        self.assertTrue(report["apng_hold_pixels_exact"])
        self.assertEqual(report["frames"][1]["gif_region_differences"]["center"]["max_absolute_channel_error_8bit"], 0)

    def test_explicit_dialog_closeup_retains_source_pixels_and_gallery_dimensions(self):
        from PIL import Image
        Image.new("RGB", (100, 100), (12, 34, 56)).save(self.root/"one.png")
        self.story["canvas"] = [100, 100]
        self.story["frames"][1]["presentation"] = "dialog-closeup"
        self.write()
        out = self.root/"output"
        report = self.module.create_demo(self.path, out)
        self.assertTrue(report["frames"][1]["captured_pixels_exact"])
        self.assertEqual(report["frames"][1]["presentation"], "dialog-closeup")
        with Image.open(out/"stills/two.png") as original:
            self.assertEqual(original.size, (12, 8))


if __name__ == "__main__":
    unittest.main()
