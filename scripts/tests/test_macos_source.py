"""Local source-root isolation checks using synthetic headers, not Mac acceptance."""
from pathlib import Path
import importlib.util
import os
import shutil
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

SCRIPTS = Path(__file__).parents[1]
sys.path.insert(0, str(SCRIPTS))
from native_ci_acceptance import clean_environment
from stage_macos_source import stage
import test_package_native as fixtures

spec = importlib.util.spec_from_file_location("source_test_engine", SCRIPTS.parent / "api/colorcorrect.py")
engine_api = importlib.util.module_from_spec(spec)
sys.path.insert(0, str(SCRIPTS.parent / "api"))
try:
    spec.loader.exec_module(engine_api)
finally:
    sys.path.pop(0)


class MacSourceTests(unittest.TestCase):
    def inputs(self, root):
        fixtures.NativePackageTests()._inputs(root, "osx-arm64")
        output = root / "local build 'quoted'"
        shutil.copytree(root / "runtime", output)
        (output / "app").mkdir()
        (output / "app/ColorCorrect.App").write_bytes(fixtures.executable("Darwin", "arm64"))
        worker = root / "engine/target/release/ccp-engine"
        worker.parent.mkdir(parents=True)
        worker.write_bytes(fixtures.executable("Darwin", "arm64"))
        return output

    def test_gui_and_explicit_sdk_use_the_same_checked_private_root(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            output = self.inputs(root)
            stage(root, output)
            self.assertEqual((output / "ccp-engine").read_bytes(), fixtures.executable("Darwin", "arm64"))
            self.assertTrue((output / "models/manifest.json").is_file())
            self.assertTrue((output / "refs/neutrals.json").is_file())
            with patch.dict(os.environ, {"ORT_DYLIB_PATH": "external/runtime"}, clear=True):
                ci = clean_environment(output, "osx-arm64")
            # Exercise the real SDK environment builder with Mac OS selection;
            # no target executable is run on the Windows component-test host.
            worker = SimpleNamespace(root=output, executable=output / "ccp-engine", _explicit_root=True)
            fake_os = SimpleNamespace(name="posix", environ={"ORT_DYLIB_PATH": "external/runtime"}, pathsep=":")
            with patch.object(engine_api, "os", fake_os), patch.object(engine_api.sys, "platform", "darwin"):
                sdk = engine_api.Engine._environment(worker)
            self.assertEqual(sdk["ORT_DYLIB_PATH"], ci["ORT_DYLIB_PATH"])
            self.assertEqual(ci["CCP_ENGINE_EXE"], str(output / "ccp-engine"))
            self.assertEqual(sdk["CCP_NEW_UI_ROOT"], str(output))
            self.assertNotIn("external", sdk["ORT_DYLIB_PATH"])
            # A later source build cannot silently replace this local worker.
            (root / "engine/target/release/ccp-engine").write_bytes(b"later build")
            self.assertEqual((output / "ccp-engine").read_bytes(), fixtures.executable("Darwin", "arm64"))

    def test_corrupt_runtime_is_rejected_before_staging_worker(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            output = self.inputs(root)
            (output / "libonnxruntime.dylib").write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "checksum mismatch"):
                stage(root, output)
            self.assertFalse((output / "ccp-engine").exists())

    def test_intel_worker_is_rejected_even_when_runtime_is_arm64(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            output = self.inputs(root)
            (root / "engine/target/release/ccp-engine").write_bytes(fixtures.executable("Darwin", "x64"))
            with self.assertRaisesRegex(ValueError, "architecture does not match"):
                stage(root, output)

    def test_output_inside_copied_resources_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            self.inputs(root)
            output = root / "models/local-build"
            output.mkdir()
            with self.assertRaisesRegex(ValueError, "inside copied"):
                stage(root, output)


if __name__ == "__main__":
    unittest.main()
