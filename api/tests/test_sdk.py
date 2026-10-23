"""Public SDK behavior exercised through pipes, capability checks, and real files."""
from dataclasses import replace
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from colorcorrect import (Client, ReferenceProfile, CalibrationOptions, ApplyOptions, DetectionOptions,
    FFCOptions, NeuralNetworkOptions, InvalidInputError, BatchProcessingError, ClosedClientError,
    UnsupportedCapabilityError, ProcessingTimeoutError, MissingRuntimeError)

ROOT = Path(__file__).resolve().parents[2]
FIXTURE = Path(__file__).with_name("sdk_fake_worker.py")
POPEN = subprocess.Popen


class AtomicJsonTests(unittest.TestCase):
    @unittest.skipUnless(os.name == "nt", "Windows file sharing is required")
    def test_real_windows_reader_can_close_before_atomic_replacement(self):
        import colorcorrect_sdk as sdk
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "model.json"
            old = b'{"old": true}'
            path.write_bytes(old)
            reader = path.open("rb")
            failed = threading.Event()
            observed = []
            original_replace = os.replace
            def replace(source, destination):
                try:
                    return original_replace(source, destination)
                except OSError as error:
                    observed.append(error.winerror)
                    self.assertEqual(path.read_bytes(), old)
                    failed.set()
                    raise
            def release_reader():
                failed.wait(timeout=5)
                time.sleep(0.05)
                reader.close()
            release = threading.Thread(target=release_reader)
            release.start()
            try:
                with patch.object(sdk.os, "replace", side_effect=replace):
                    sdk._write_json(path, {"new": True}, overwrite=True)
            finally:
                failed.set()
                reader.close()
                release.join()
            self.assertTrue(observed, "test must observe a real Windows replacement denial")
            self.assertTrue(set(observed) <= {5, 32, 33})
            self.assertEqual(json.loads(path.read_text(encoding="utf-8")), {"new": True})
            self.assertEqual(list(Path(folder).glob(".colorcorrect-*.json")), [])

    @unittest.skipUnless(os.name == "nt", "Windows read-only replacement semantics are required")
    def test_real_read_only_destination_remains_unchanged_after_bounded_retry(self):
        import colorcorrect_sdk as sdk
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "model.json"
            old = b'{"old": true}'
            path.write_bytes(old)
            path.chmod(stat.S_IREAD)
            try:
                with patch.object(sdk.os, "replace", wraps=os.replace) as replace:
                    with self.assertRaises(PermissionError) as error:
                        sdk._write_json(path, {"new": True}, overwrite=True)
                    self.assertEqual(error.exception.winerror, 5)
                    self.assertEqual(replace.call_count, 6)
                self.assertEqual(path.read_bytes(), old)
                self.assertEqual(list(Path(folder).glob(".colorcorrect-*.json")), [])
            finally:
                path.chmod(stat.S_IREAD | stat.S_IWRITE)

    def test_windows_replacement_retries_are_bounded_and_preserve_old_json(self):
        import colorcorrect_sdk as sdk
        from types import SimpleNamespace
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "model.json"
            old = b'{"old": true}'
            path.write_bytes(old)
            for code in (5, 32, 33):
                error = PermissionError("destination remains unavailable")
                error.winerror = code
                with self.subTest(code=code), patch.object(sdk, "os", SimpleNamespace(
                    name="nt", fdopen=os.fdopen, replace=Mock(side_effect=error))), \
                        patch.object(sdk.time, "sleep") as sleep:
                    with self.assertRaises(PermissionError):
                        sdk._write_json(path, {"new": True}, overwrite=True)
                    self.assertEqual(sdk.os.replace.call_count, 6)
                    self.assertAlmostEqual(sum(call.args[0] for call in sleep.call_args_list), 0.31)
                self.assertEqual(path.read_bytes(), old)
                self.assertEqual(list(Path(folder).glob(".colorcorrect-*.json")), [])

    def test_unrelated_or_non_windows_errors_are_not_retried(self):
        import colorcorrect_sdk as sdk
        from types import SimpleNamespace
        for platform, code in (("posix", 5), ("nt", 2), ("nt", None)):
            error = OSError("not a transient Windows sharing failure")
            error.winerror = code
            fake_os = SimpleNamespace(name=platform, replace=Mock(side_effect=error))
            with self.subTest(platform=platform, code=code), patch.object(sdk, "os", fake_os), \
                    patch.object(sdk.time, "sleep") as sleep:
                with self.assertRaises(OSError):
                    sdk._replace_json_atomic("temporary", Path("model.json"))
                self.assertEqual(fake_os.replace.call_count, 1)
                sleep.assert_not_called()

    def test_serialization_and_cleanup_errors_cannot_destroy_old_json_or_mask_cause(self):
        import colorcorrect_sdk as sdk
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "model.json"
            old = b'{"old": true}'
            path.write_bytes(old)
            with self.assertRaises(ValueError):
                sdk._write_json(path, {"invalid": float("nan")}, overwrite=True)
            self.assertEqual(path.read_bytes(), old)
            self.assertEqual(list(Path(folder).glob(".colorcorrect-*.json")), [])
            cause = PermissionError("original replacement failure")
            with patch.object(sdk, "_replace_json_atomic", side_effect=cause), \
                    patch.object(Path, "unlink", side_effect=OSError("cleanup failure")):
                with self.assertRaises(PermissionError) as result:
                    sdk._write_json(path, {"new": True}, overwrite=True)
                self.assertIs(result.exception, cause)
            self.assertEqual(path.read_bytes(), old)
            for temporary in Path(folder).glob(".colorcorrect-*.json"):
                temporary.unlink()


class ReferenceTests(unittest.TestCase):
    def test_duplicate_json_interpretation_fields_are_rejected(self):
        profile = ReferenceProfile.from_values([(50, 0, 0)] * 24)
        text = json.dumps(profile.to_dict())
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "ambiguous.json"
            path.write_text(text[:-1] + ', "white_point": "d65"}', encoding="utf8")
            with self.assertRaisesRegex(InvalidInputError, "duplicate JSON field: white_point"):
                ReferenceProfile.from_json(path)
            self.assertEqual(profile.white_point, "d50")

    def test_all_layouts_and_immutable_values(self):
        for chart, count in (("classic", 24), ("passport", 24), ("spyder", 24), ("digital-sg", 140)):
            values = [[index % 101, 0, -1] for index in range(count)]
            profile = ReferenceProfile.from_values(values, chart=chart)
            values[0][0] = 999
            self.assertEqual(profile.values[0][0], 0)
            self.assertEqual(len(profile.values), count)

    def test_csv_and_paste_orders_and_coordinates(self):
        text = "L,a,b\n" + "\n".join(f"{index},0,0" for index in range(24))
        profile = ReferenceProfile.from_text(text, order="column-major")
        self.assertEqual(profile.order, "column-major")
        rows = [f"{r}\t{c}\t{r * 10 + c}\t0\t0" for r in range(1, 5) for c in range(1, 7)]
        coordinate = ReferenceProfile.from_text("row\tcolumn\tL\ta\tb\n" + "\n".join(reversed(rows)))
        self.assertEqual(coordinate.order, "row-major")
        self.assertEqual(coordinate.values[0], (11, 0, 0))
        self.assertEqual(coordinate.values[-1], (46, 0, 0))
        named = [f"{chr(64 + c)}{r},{r * 10 + c},0,0" for r in range(1, 5) for c in range(1, 7)]
        self.assertEqual(ReferenceProfile.from_text("cell,L,a,b\n" + "\n".join(reversed(named))).values, coordinate.values)
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "values.csv"
            path.write_text(text, encoding="utf-8")
            self.assertEqual(ReferenceProfile.from_csv(path).values, profile.values)

    def test_bad_tables_are_atomic(self):
        valid = ReferenceProfile.from_values([(50, 0, 0)] * 24)
        for text in ("", "L,a,b\n1,2,3", "\n".join(["1,1,50,0,0"] * 24),
                     "\n".join(["50,nan,0"] * 24), "nonsense\n" * 24):
            with self.subTest(text=text[:20]), self.assertRaises(InvalidInputError):
                ReferenceProfile.from_text(text)
        self.assertEqual(valid.values[0], (50, 0, 0))

    def test_header_must_match_declared_encoding(self):
        for header, space in (("R,G,B", "lab"), ("L,a,b", "xyz"), ("X,Y,Z", "srgb"),
                              ("row,column,R,G,B", "lab")):
            text = header + "\n" + "\n".join(["50,0,0"] * 24)
            with self.subTest(header=header, space=space), self.assertRaises(InvalidInputError):
                ReferenceProfile.from_text(text, color_space=space)

    def test_spaces_scales_and_json_roundtrip(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "日本.json"
            profile = ReferenceProfile.from_values([(1.2, 0.1, 0.3)] * 24, color_space="xyz", scale=1)
            profile.to_json(path)
            self.assertEqual(ReferenceProfile.from_json(path), profile)
            with self.assertRaises(InvalidInputError):
                profile.to_json(path)
            profile.to_json(path, overwrite=True)
            payload = profile.to_dict()
            payload["observer"] = 10
            path.write_text(json.dumps(payload), encoding="utf-8")
            with self.assertRaises(InvalidInputError):
                ReferenceProfile.from_json(path)
            payload.pop("observer")
            payload["version"] = 9
            path.write_text(json.dumps(payload), encoding="utf-8")
            with self.assertRaises(UnsupportedCapabilityError):
                ReferenceProfile.from_json(path)
        for options in ({"scale": 100}, {"white_point": "unknown"}, {"color_space": "rgb"},
                        {"revision": 0}, {"color_space": "srgb", "scale": 100}, {"values": [(1, 2, 3)]},
                        {"values": [(101, 0, 0)] * 24},
                        {"color_space": "xyz", "values": [(1, -0.1, 0)] * 24},
                        {"color_space": "srgb", "values": [(1.01, 0, 0)] * 24}):
            with self.subTest(options=options), self.assertRaises(InvalidInputError):
                ReferenceProfile(**{"values": [(50, 0, 0)] * 24, **options})


class SDKTests(unittest.TestCase):
    def test_explicit_macos_bundle_root_discovers_its_native_resources(self):
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "ColorCorrect Studio.app"
            resources = bundle / "Contents/MacOS"
            (resources / "refs").mkdir(parents=True)
            (resources / "refs/neutrals.json").write_text("{}", encoding="utf8")
            with_client = Client(sys.executable, root=bundle)
            try:
                self.assertEqual(with_client.engine.root, resources.resolve())
            finally:
                with_client.close()

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="colorcorrect-sdk-", suffix="-日本")
        self.folder = Path(self.temp.name)
        for name in ("chart.jpg", "multi.jpg", "white.jpg", "scene.jpg", "bad.jpg", "scene.mp4", "slow.jpg"):
            (self.folder / name).write_text("fixture", encoding="utf-8")
        def launch(args, **kwargs):
            return POPEN([sys.executable, str(FIXTURE)], **kwargs)
        self.mock = patch("colorcorrect.subprocess.Popen", side_effect=launch)
        self.mock.start()
        self.client = Client(sys.executable, root=ROOT, timeout=5)
        self.profile = ReferenceProfile.from_values([(50, 0, 0)] * 24)

    def tearDown(self):
        self.client.close()
        self.mock.stop()
        self.temp.cleanup()

    def fit(self, **options):
        return self.client.calibrate(self.folder / "chart.jpg", output_dir=self.folder / "results", **options)

    def test_fast_mode_defaults_on_and_requires_worker_support(self):
        self.assertTrue(ApplyOptions().to_dict()["fast_mode"])
        self.assertTrue(CalibrationOptions().to_dict()["fast_mode"])
        self.assertNotIn("fast_mode", ApplyOptions(fast_mode=False).to_dict())
        self.assertNotIn("fast_mode", CalibrationOptions(fast_mode=False).to_dict())
        for cls in (ApplyOptions, CalibrationOptions):
            for value in ("true", 1, None):
                with self.assertRaises(InvalidInputError):
                    cls(fast_mode=value).to_dict()
        model = self.fit(options=CalibrationOptions(fast_mode=True))
        result = model.apply(self.folder / "scene.jpg", output_dir=self.folder,
                             options=ApplyOptions(fast_mode=True))
        self.assertIs(result.raw_reply["echo"]["fast_mode"], True)
        self.client.engine.ready["capabilities"] = []
        with self.assertRaisesRegex(UnsupportedCapabilityError, "fast mode"):
            model.apply(self.folder / "scene.jpg", output_dir=self.folder,
                        options=ApplyOptions(fast_mode=True))
        with self.assertRaisesRegex(UnsupportedCapabilityError, "fast mode"):
            self.fit(options=CalibrationOptions(fast_mode=True))
        # Explicit standard mode still supports workers predating fast mode.
        result = model.apply(self.folder / "scene.jpg", output_dir=self.folder,
                             options=ApplyOptions(fast_mode=False))
        self.assertNotIn("fast_mode", result.raw_reply["echo"])

    def test_public_workflow_reload_and_no_overwrites(self):
        with self.client:
            model = self.fit(reference=self.profile)
            self.assertEqual(model.metrics["delta_e_2000"]["mean_after"], 1.2)
            single = model.apply(self.folder / "scene.jpg", output_dir=self.folder / "applied")
            batch = model.apply([self.folder / "scene.jpg"], output_dir=self.folder / "applied")
            self.assertEqual(type(single), type(batch))
            self.assertNotEqual(single.folder, batch.folder)
            self.assertTrue(all(path.is_file() for path in single.outputs))
            cube = model.export_lut(self.folder / "grade.cube", size=2)
            self.assertTrue(cube.path.is_file())
            with self.assertRaises(InvalidInputError):
                model.export_lut(cube.path)
            model.export_lut(cube.path, overwrite=True)
            loaded = self.client.load_calibration(model.path)
            self.assertEqual(loaded.metrics, model.metrics)
            self.assertEqual(model.backends.actual["apply"], "cpu")
            self.assertEqual(model.backends.actual["detect"], "cpu")
            self.assertEqual(model.backends.diagnostics["detection"]["backend"], "onnx")
            self.assertEqual(model.backends.requested, "auto")
        with self.assertRaises(ClosedClientError):
            model.apply(self.folder / "scene.jpg", output_dir=self.folder / "applied")
        with self.assertRaises(ClosedClientError):
            self.client.__enter__()

    def test_multi_chart_identity_assignments_survive_selection_order(self):
        detected = self.client.detect(self.folder / "multi.jpg")
        self.assertEqual(detected.backends.actual["detect"], "cpu")
        self.assertEqual(detected.backends.diagnostics["backend"], "onnx")
        first, second = detected.charts
        other = replace(self.profile, id="other")
        result = self.client.calibrate(detected.image, detection=detected,
            chart_ids=[second.id, first.id], references={first.id: self.profile, second.id: other},
            output_dir=self.folder / "multi")
        echo = result.raw_reply["echo"]
        self.assertTrue(echo["use_all"])
        self.assertEqual(echo["handshake"]["charts"][0]["chart_id"], second.id)
        self.assertEqual({row["chart_id"] for row in echo["reference_assignments"]}, {first.id, second.id})
        inferred = self.client.calibrate(detected.image, detection=detected,
            references={second.id: other, first.id: self.profile}, output_dir=self.folder / "inferred")
        self.assertEqual(inferred.raw_reply["echo"]["handshake"]["charts"][0]["chart_id"], second.id)
        with self.assertRaises(InvalidInputError):
            self.client.calibrate(detected.image, detection=detected,
                reference=self.profile, output_dir=self.folder / "ambiguous")
        with self.assertRaises(InvalidInputError):
            self.client.calibrate(detected.image, detection=detected, chart_ids=[first.id, second.id],
                reference=self.profile, output_dir=self.folder / "invalid")
        with self.assertRaises(InvalidInputError):
            self.client.calibrate(detected.image, detection=detected, chart_ids=[first.id, second.id],
                references={first.id: self.profile}, output_dir=self.folder / "invalid")

    def test_passport_compatibility_and_wrong_chart_rejected(self):
        self.fit(reference=replace(self.profile, chart="passport"))
        with self.assertRaises(InvalidInputError):
            self.fit(reference=replace(self.profile, chart="spyder"))

    def test_stale_or_unrelated_detection_cannot_fit(self):
        detected = self.client.detect(self.folder / "chart.jpg")
        detected.handshake["charts"][0]["chart_id"] = "tampered"
        self.assertNotEqual(detected.handshake["charts"][0]["chart_id"], "tampered")
        with self.assertRaises(InvalidInputError):
            self.client.calibrate(self.folder / "multi.jpg", detection=detected, output_dir=self.folder)
        detected.image.write_text("changed image size", encoding="utf-8")
        with self.assertRaises(InvalidInputError):
            self.client.calibrate(detected.image, detection=detected, output_dir=self.folder)

    def test_old_worker_rejects_custom_but_keeps_builtins(self):
        self.client.close()
        with patch.dict(os.environ, {"CCP_TEST_OLD_WORKER": "1"}):
            self.client = Client(sys.executable, root=ROOT)
            with self.assertRaises(UnsupportedCapabilityError):
                self.fit(reference=self.profile, options=CalibrationOptions(fast_mode=False))
            self.fit(options=CalibrationOptions(fast_mode=False))

    def test_ffc_survives_model_reload_and_explicit_disable(self):
        model = self.fit(white=self.folder / "white.jpg", options=CalibrationOptions(ffc=FFCOptions(method="pls")))
        loaded = self.client.load_calibration(model.path)
        result = loaded.apply(self.folder / "scene.jpg", output_dir=self.folder)
        echo = result.raw_reply["echo"]
        self.assertTrue(echo["do_ffc"])
        self.assertEqual(echo["ffc"]["method"], "pls")
        self.assertEqual(echo["white"], str((self.folder / "white.jpg").resolve()))
        (self.folder / "white.jpg").unlink()
        with self.assertRaises(InvalidInputError):
            loaded.apply(self.folder / "scene.jpg", output_dir=self.folder)
        loaded.apply(self.folder / "scene.jpg", output_dir=self.folder, options=ApplyOptions(do_ffc=False))
        with self.assertRaises(InvalidInputError):
            self.fit(options=CalibrationOptions(do_ffc=True))

    def test_inactive_settings_do_not_block_other_operations(self):
        model = self.fit(white=self.folder / "missing.jpg", options=CalibrationOptions(
            do_ffc=False, nn=NeuralNetworkOptions(hidden_layers=(1,)), ffc=FFCOptions(smooth=2)))
        model.apply(self.folder / "scene.jpg", output_dir=self.folder,
                    options=ApplyOptions(do_ffc=False, ffc=FFCOptions(smooth=2),
                                         white=self.folder / "missing.jpg"))
        self.assertNotIn("nn", model.raw_reply["echo"])
        self.assertNotIn("ffc", model.raw_reply["echo"])

    def test_model_handle_rejects_changed_file_until_explicit_reload(self):
        model = self.fit()
        saved = json.loads(model.path.read_text(encoding="utf8"))
        saved["extra_provenance"] = "different saved file"
        model.path.write_text(json.dumps(saved), encoding="utf8")
        with self.assertRaisesRegex(InvalidInputError, "changed on disk"):
            model.apply(self.folder / "scene.jpg", output_dir=self.folder)
        with self.assertRaisesRegex(InvalidInputError, "changed on disk"):
            model.export_lut(self.folder / "stale.cube")
        self.client.load_calibration(model.path).apply(self.folder / "scene.jpg", output_dir=self.folder)

    def test_changed_ffc_white_requires_explicit_selection(self):
        white = self.folder / "white.jpg"
        model = self.fit(white=white)
        white.write_text("changed reference", encoding="utf8")
        with self.assertRaisesRegex(InvalidInputError, "white image changed"):
            model.apply(self.folder / "scene.jpg", output_dir=self.folder)
        model.apply(self.folder / "scene.jpg", output_dir=self.folder, options=ApplyOptions(white=white))

    def test_batch_partial_and_preflight_missing_image_outcomes(self):
        model = self.fit()
        images = [self.folder / "scene.jpg", self.folder / "bad.jpg", self.folder / "missing.jpg"]
        with self.assertRaises(BatchProcessingError) as caught:
            model.apply(images, output_dir=self.folder)
        result = caught.exception.result
        self.assertEqual(len(result.images), 3)
        self.assertEqual(len(result.outputs), 1)
        self.assertEqual(len(result.failures), 2)
        continued = model.apply(images, output_dir=self.folder, on_error="continue")
        self.assertEqual(len(continued.failures), 2)
        all_bad = model.apply([images[-1]], output_dir=self.folder, on_error="continue")
        self.assertEqual(len(all_bad.failures), 1)
        result = model.apply(images[0], output_dir=self.folder, options=ApplyOptions(write_output=False))
        self.assertFalse(result.outputs)

    def test_legacy_models_and_ffc_asset_requirements(self):
        path = self.folder / "legacy.json"
        path.write_text(json.dumps({"kind": "rpcc2", "correction": {}, "do_ffc": True}), encoding="utf-8")
        model = self.client.load_calibration(path)
        self.assertIsNone(model.metrics)
        self.assertTrue(model.warnings)
        with self.assertRaises(InvalidInputError):
            model.apply(self.folder / "scene.jpg", output_dir=self.folder)
        model.apply(self.folder / "scene.jpg", output_dir=self.folder, options=ApplyOptions(white=self.folder / "white.jpg"))

    def test_cc_disabled_and_standalone_legacy_models_load(self):
        path = self.folder / "model.json"
        for saved in ({"kind": "rpcc2", "correction": None, "do_cc": False},
                      {"kind": "linear", "feature_count": 3, "coefficients": [1, 0, 0]}):
            with self.subTest(saved=saved):
                path.write_text(json.dumps(saved), encoding="utf-8")
                model = self.client.load_calibration(path)
                self.assertIsNone(model.metrics)
                self.assertEqual(model.path, path.resolve())
                with self.assertRaisesRegex(InvalidInputError, "WB.*absent"):
                    model.apply(self.folder / "scene.jpg", output_dir=self.folder,
                                options=ApplyOptions(do_wb=True))

    def test_lut_warns_about_spatial_ffc(self):
        model = self.fit(white=self.folder / "white.jpg")
        cube = model.export_lut(self.folder / "grade.cube", size=2)
        self.assertTrue(cube.warnings)
    def test_option_validation_happens_without_starting_worker(self):
        for call in (lambda: self.fit(options=CalibrationOptions(model="typo")),
                     lambda: self.fit(options=CalibrationOptions(model="nn", nn=NeuralNetworkOptions(hidden_layers=(1,)))),
                     lambda: self.fit(white=self.folder / "white.jpg", options=CalibrationOptions(ffc=FFCOptions(smooth=2))),
                     lambda: self.fit(options=CalibrationOptions(input_space="acescg")),
                     lambda: self.client.detect(self.folder / "chart.jpg", options=DetectionOptions(crop=0)),
                     lambda: self.fit(output_dir=None)):
            with self.subTest(call=call), self.assertRaises((InvalidInputError, TypeError)):
                call()
        self.assertIsNone(self.client.engine._process)

    def test_timeout_and_missing_runtime_categories(self):
        self.client.close()
        self.client = Client(sys.executable, root=ROOT, timeout=0.1)
        with self.assertRaises(ProcessingTimeoutError):
            self.client.detect(self.folder / "slow.jpg")
        self.assertIsNone(self.client.engine._process)
        with self.assertRaises(MissingRuntimeError):
            Client(self.folder / "absent.exe", root=ROOT)


if __name__ == "__main__":
    unittest.main()
