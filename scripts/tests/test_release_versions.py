"""Native product and SDK versions evolve independently but remain internally coherent."""
import ast
import json
import plistlib
from pathlib import Path
import re
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))
from package_support import native_version
from package_deb import build_deb
from native_ci_acceptance import emitted_package_versions


def table_version(path: Path, section: str) -> str:
    # These manifests declare literal versions; no TOML runtime dependency is needed.
    table = re.search(r"(?ms)^\[" + re.escape(section) + r"\]\s*\n(.*?)(?=^\[|\Z)", path.read_text())
    if table is None:
        raise AssertionError(f"Missing [{section}] in {path}")
    value = re.search(r'(?m)^version\s*=\s*"([^"]+)"\s*$', table[1])
    if value is None:
        raise AssertionError(f"Missing literal version in {path}")
    return value[1]


class ReleaseVersions(unittest.TestCase):
    def test_native_product_and_separate_sdk_versions_are_coherent(self):
        root = SCRIPTS.parent
        product = native_version(root)
        self.assertEqual(ET.parse(root / "Directory.Build.props").findtext(".//Version"), product)
        app = ET.parse(root / "app/ColorCorrect.App/ColorCorrect.App.csproj")
        self.assertEqual(app.findtext(".//InformationalVersion"), product)
        lock = (root / "engine/Cargo.lock").read_text()
        crates = ("ccp_core", "ccp_detect", "ccp_bridge", "ccp_wgpu_infer")
        for crate in crates:
            with self.subTest(crate=crate):
                self.assertEqual(table_version(root / f"engine/crates/{crate}/Cargo.toml", "package"), product)
                locked = re.search(r'(?m)^name = "' + re.escape(crate) + r'"\r?\nversion = "([^"]+)"', lock)
                self.assertIsNotNone(locked, f"Missing local crate {crate} in Cargo.lock")
                self.assertEqual(locked[1], product)
        sdk = table_version(root / "api/pyproject.toml", "project")
        module = ast.parse((root / "api/colorcorrect.py").read_text(encoding="utf-8-sig"))
        declared = [ast.literal_eval(node.value) for node in module.body if isinstance(node, ast.Assign)
                    and any(isinstance(target, ast.Name) and target.id == "__version__" for target in node.targets)]
        self.assertEqual(declared, [sdk])
        installer = (root / "installer/app.iss").read_text()
        self.assertIn("AppVersion={#MyAppVersion}", installer)
        self.assertIn("VersionInfoVersion={#MyAppVersion}", installer)
        self.assertIn("VersionInfoProductVersion={#MyAppVersion}", installer)
        self.assertIn("OutputBaseFilename=ColorCorrect-Studio-{#MyAppVersion}-{#PackageRid}-Setup", installer)

    def test_debian_emitted_control_and_filename_use_native_version(self):
        product = native_version(SCRIPTS.parent)
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            payload = folder / "payload"
            payload.mkdir()
            for rid, architecture in (("linux-x64", "amd64"), ("linux-arm64", "arm64")):
                output = folder / rid
                output.mkdir()
                # The real native CI checks dpkg metadata; this portable test does not
                # require dpkg or permission to create POSIX symlinks on Windows.
                with patch("package_deb.Path.symlink_to"), patch("package_deb.subprocess.run") as build:
                    archive = build_deb(payload, output, product, rid)
                control = (output / "deb-root/DEBIAN/control").read_text()
                self.assertIn(f"\nVersion: {product}\n", control)
                self.assertIn(f"\nArchitecture: {architecture}\n", control)
                self.assertEqual(archive.name, f"colorcorrect-studio_{product}_{architecture}.deb")
                self.assertEqual(build.call_args.args[0][-1], str(archive))

    def test_native_gate_rejects_mismatched_emitted_versions(self):
        with tempfile.TemporaryDirectory() as temporary:
            package = Path(temporary)
            (package / "candidate-Setup.exe").touch()
            (package / "candidate.deb").touch()
            from subprocess import CompletedProcess
            good = CompletedProcess([], 0, json.dumps({"FileVersion": "0.2.0.0    ", "ProductVersion": "0.2.0+revision    "}))
            wrong = CompletedProcess([], 0, json.dumps({"FileVersion": "0.1.0.0", "ProductVersion": "0.1.0"}))
            with patch("native_ci_acceptance.subprocess.run", return_value=good):
                self.assertEqual(set(emitted_package_versions(package, package, "win-x64", "0.2.0")),
                                 {"application", "installer"})
            with patch("native_ci_acceptance.subprocess.run", return_value=wrong):
                with self.assertRaisesRegex(RuntimeError, "does not match"):
                    emitted_package_versions(package, package, "win-arm64", "0.2.0")
            with patch("native_ci_acceptance.subprocess.check_output", return_value="0.1.0\n"):
                with self.assertRaisesRegex(RuntimeError, "does not match"):
                    emitted_package_versions(package, package, "linux-arm64", "0.2.0")
            with patch("native_ci_acceptance.subprocess.check_output", return_value="0.2.0\n"):
                self.assertEqual(emitted_package_versions(package, package, "linux-x64", "0.2.0"),
                                 {"debian": "0.2.0"})

    def test_macos_emitted_bundle_versions_and_minimum_os_are_verified(self):
        version=native_version(SCRIPTS.parent)
        with tempfile.TemporaryDirectory() as temporary:
            package=Path(temporary)
            contents=package/'ColorCorrect Studio.app/Contents'
            payload=contents/'Resources/runtime';payload.mkdir(parents=True)
            metadata={'CFBundleShortVersionString':version,'CFBundleVersion':version,'LSMinimumSystemVersion':'14.0'}
            def write(values):
                with (contents/'Info.plist').open('wb') as stream:plistlib.dump(values,stream)
            write(metadata)
            self.assertEqual(emitted_package_versions(package,payload,'osx-arm64',version),{'application_bundle':metadata})
            for field,value in [('CFBundleShortVersionString','0.1.0'),('CFBundleVersion','0.1.0'),('LSMinimumSystemVersion','13.0')]:
                with self.subTest(field=field):
                    write(dict(metadata,**{field:value}))
                    with self.assertRaisesRegex(RuntimeError,'match'):
                        emitted_package_versions(package,payload,'osx-arm64',version)


if __name__ == "__main__":
    unittest.main()
