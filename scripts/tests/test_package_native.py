"""Packaging contract tests with synthetic executable headers; not native acceptance."""
from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest
import hashlib
import shutil
from unittest.mock import patch


spec = importlib.util.spec_from_file_location("package_native", Path(__file__).parents[1] / "package_native.py")
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


def executable(system: str, architecture: str) -> bytes:
    if system == "Windows":
        data = bytearray(70)
        data[:2] = b"MZ"
        struct.pack_into("<I", data, 0x3C, 64)
        data[64:68] = b"PE\0\0"
        struct.pack_into("<H", data, 68, 0x8664 if architecture == "x64" else 0xAA64)
    elif system == "Linux":
        data = bytearray(64)
        data[:7] = b"\x7fELF\x02\x01\x01"
        struct.pack_into("<H", data, 18, 62 if architecture == "x64" else 183)
    else:
        data = bytearray(32)
        data[:4] = b"\xcf\xfa\xed\xfe"
        struct.pack_into("<I", data, 4, 0x01000007 if architecture == "x64" else 0x0100000C)
    return bytes(data)


def universal_macho() -> bytes:
    first = executable("Darwin", "x64")
    second = executable("Darwin", "arm64")
    return (struct.pack(">II", 0xCAFEBABE, 2) +
            struct.pack(">IIIII", 0x01000007, 0, 48, len(first), 0) +
            struct.pack(">IIIII", 0x0100000C, 0, 80, len(second), 0) + first + second)


class NativePackageTests(unittest.TestCase):
    def test_all_supported_architectures_and_wrong_target_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "native"
            for system, architecture, _ in package.TARGETS.values():
                with self.subTest(system=system, architecture=architecture):
                    path.write_bytes(executable(system, architecture))
                    package.require_architecture(path, (system, architecture))
                    with self.assertRaises(ValueError):
                        package.require_architecture(path, (system, "arm64" if architecture == "x64" else "x64"))

    def test_universal_macos_requires_valid_slice_headers(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "universal"
            path.write_bytes(universal_macho())
            self.assertEqual(package.executable_architectures(path), {("Darwin", "x64"), ("Darwin", "arm64")})
            damaged = bytearray(universal_macho())
            damaged[80:84] = b"junk"
            path.write_bytes(damaged)
            with self.assertRaisesRegex(ValueError, "slice header"):
                package.executable_architectures(path)

    def test_truncated_and_mislabelled_headers_fail_cleanly(self):
        invalid = [b"", b"MZ", b"\x7fELF", b"\xcf\xfa\xed\xfe", struct.pack(">II", 0xCAFEBABE, 64)]
        elf32 = bytearray(executable("Linux", "x64"))
        elf32[4] = 1
        invalid.append(elf32)
        invalid_endian = bytearray(executable("Linux", "arm64"))
        invalid_endian[5] = 0
        invalid.append(invalid_endian)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "invalid"
            for contents in invalid:
                with self.subTest(header=contents[:8]):
                    path.write_bytes(contents)
                    with self.assertRaises(ValueError):
                        package.executable_architectures(path)

    def _inputs(self, root: Path, rid: str):
        system, architecture, _ = package.TARGETS[rid]
        (root / "app/ColorCorrect.App").mkdir(parents=True)
        (root / "app/ColorCorrect.App/ColorCorrect.App.csproj").write_text("<Project><PropertyGroup><Version>2.0.0</Version></PropertyGroup></Project>")
        (root / "refs").mkdir()
        (root / "refs/neutrals.json").write_text("{}")
        (root / "models/opt2").mkdir(parents=True)
        for name in package.MODELS:
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"fixture")
        (root / "models/manifest.json").write_text(json.dumps({"files": [{"path": name, "sha256": hashlib.sha256(b"fixture").hexdigest()} for name in package.MODELS]}))
        for name in package.REQUIRED_NOTICES:
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("fixture notice")
        (root / "refs/digital_sg_lab_d50.json").write_text("{}")
        shutil.copytree(Path(__file__).parents[2] / "installer", root / "installer")
        worker = root / "worker"
        worker.write_bytes(executable(system, architecture))
        runtime = root / "runtime"
        runtime.mkdir()
        for name in package.RUNTIMES[system]:
            (runtime / name).write_bytes(executable(system, architecture))
        notices = runtime / "licenses/test-runtime"
        notices.mkdir(parents=True)
        (notices / "LICENSE").write_text("notice")
        (runtime / "gpu-runtime-manifest.json").write_text(json.dumps({"rid": rid, "packages": [{"id": "test-runtime"}],
            "files": {name: hashlib.sha256((runtime / name).read_bytes()).hexdigest() for name in package.RUNTIMES[system]}}))
        wheel = root / "colorcorrect_studio-2.2.1-py3-none-any.whl"
        wheel.write_bytes(b"synthetic wheel fixture")
        (root / "engine").mkdir()
        (root / "engine/Cargo.lock").write_text("synthetic lock")
        dependency_notices = root / "dependency-notices"
        dependency_notices.mkdir()
        (dependency_notices / "LICENSE.txt").write_text("fixture license")
        (dependency_notices / "manifest.json").write_text(json.dumps({"rid": rid, "unresolved": [],
            "source_hashes": {"engine/Cargo.lock": hashlib.sha256(b"synthetic lock").hexdigest()},
            "published_deps_sha256": hashlib.sha256(b"{}").hexdigest(),
            "sha256": {"LICENSE.txt": hashlib.sha256(b"fixture license").hexdigest()}}))
        arguments = ["package_native.py", "--rid", rid, "--worker", str(worker), "--ort-runtime", str(runtime),
                     "--features", " ort-plane ", "--wheel", str(wheel),
                     "--dependency-notices", str(dependency_notices),
                     "--root", str(root), "--output", str(root / "dist/package")]
        if system == "Windows":
            arguments += ["--vc-redist", str(root / "vc_redist.x64.exe")]
            (root / "vc_redist.x64.exe").write_bytes(b"fixture")
        return arguments

    @staticmethod
    def _redist(source, payload, rid):
        folder = payload / "prerequisites"
        folder.mkdir()
        data = {"version": "14.44.35211.0", "sha256": "a" * 64}
        (folder / "vc-redist.json").write_text(json.dumps(data))
        return data

    def test_all_supported_payloads_have_discoverable_runtimes_and_launchers(self):
        for rid, (system, architecture, _) in package.PACKAGE_TARGETS.items():
            with self.subTest(rid=rid), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                arguments = self._inputs(root, rid)

                def publish(command, **kwargs):
                    destination = Path(command[command.index("-o") + 1])
                    destination.mkdir(parents=True, exist_ok=True)
                    name = "ColorCorrect.App.exe" if system == "Windows" else "ColorCorrect.App"
                    (destination / name).write_bytes(executable(system, architecture))
                    (destination / "ColorCorrect.App.deps.json").write_text("{}")
                    (destination / "libHarfBuzzSharp.pdb").write_bytes(b"NuGet native debug symbols")
                    (destination / "native").mkdir()
                    (destination / "native/Other.PDB").write_bytes(b"nested debug symbols")
                    (destination / "native/runtime-data.bin").write_bytes(b"required runtime data")

                with patch.object(sys, "argv", arguments), patch.object(package.platform, "system", return_value=system), \
                     patch.object(package, "native_architecture", return_value=architecture), \
                     patch.object(package, "dependency_audit", return_value={"status": "synthetic"}), \
                     patch("package_windows_runtime.stage_redist", side_effect=self._redist), \
                     patch.object(package.subprocess, "run", side_effect=publish):
                    self.assertEqual(package.main(), 0)
                output = root / "dist/package"
                stage = output / "payload"
                payload = stage / "ColorCorrect Studio.app/Contents/MacOS" if system == "Darwin" else stage
                native_name = {"Windows": "onnxruntime.dll", "Linux": "libonnxruntime.so", "Darwin": "libonnxruntime.dylib"}[system]
                self.assertTrue((payload / native_name).is_file())
                extension = ".exe" if system == "Windows" else ""
                self.assertFalse((payload / ("ffmpeg" + extension)).exists())
                self.assertFalse((payload / ("ffprobe" + extension)).exists())
                launcher = stage / "ColorCorrect-Studio.cmd" if system == "Windows" else payload / ("launch" if system == "Darwin" else "ColorCorrect-Studio")
                text = launcher.read_text(encoding="utf-8")
                self.assertIn("CCP_NEW_UI_ROOT", text)
                self.assertIn("CCP_ENGINE_EXE", text)
                self.assertIn("ORT_DYLIB_PATH", text)
                self.assertNotIn("LIBTORCH", text)
                if system != "Windows":
                    # Windows filesystems cannot represent POSIX executable bits.
                    if os.name != "nt":
                        self.assertTrue((payload / "ccp-engine").stat().st_mode & 0o111)
                    self.assertIn('"$@"', text)
                manifest = json.loads((output / "package-manifest.json").read_text())
                self.assertEqual(manifest["version"], "2.0.0")
                support = json.loads((payload / "runtime-support.json").read_text())
                self.assertEqual(support["version"], manifest["version"])
                suffix = ".zip" if system == "Windows" else ".tar.gz"
                self.assertTrue((output / f"ColorCorrect-Studio-2.0.0-{rid}{suffix}").is_file())
                self.assertEqual(manifest["native_validation"], "pending")
                self.assertIn(launcher.relative_to(stage).as_posix(), manifest["sha256"])
                self.assertFalse(any(Path(name).suffix.lower() == ".pdb" for name in manifest["sha256"]))
                self.assertFalse(any(path.suffix.lower() == ".pdb" for path in (payload / "app").rglob("*")))
                self.assertEqual((payload / "app/native/runtime-data.bin").read_bytes(), b"required runtime data")
                self.assertEqual(set(manifest["sha256"]), {path.relative_to(stage).as_posix() for path in stage.rglob("*") if path.is_file()} - {"package-manifest.json"})
                self.assertEqual(manifest["worker_features_declared"], ["ort-plane"])
                if system == "Windows":
                    self.assertTrue((output / "installer-metadata.iss").is_file())
                    self.assertIn('#define MyAppVersion "2.0.0"', (output / "installer-metadata.iss").read_text())
                    self.assertFalse((payload / "installer-metadata.iss").exists())
                    self.assertNotIn("installer-metadata.iss", manifest["sha256"])

    def test_wrong_sidecar_architecture_rejected_before_publish(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            arguments = self._inputs(root, "linux-arm64")
            (root / "runtime/libwrong.so").write_bytes(executable("Linux", "x64"))
            with patch.object(sys, "argv", arguments), patch.object(package.platform, "system", return_value="Linux"), \
                 patch.object(package, "native_architecture", return_value="arm64"), \
                 patch.object(package.subprocess, "run") as publish:
                with self.assertRaises(SystemExit):
                    package.main()
                publish.assert_not_called()
            self.assertFalse((root / "dist/package").exists())

    def test_prebuilt_app_is_architecture_checked_without_launching_build_tools(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            arguments = self._inputs(root, "linux-x64")
            published = root / "prebuilt"
            published.mkdir()
            app = published / "ColorCorrect.App"
            (published / "ColorCorrect.App.deps.json").write_text("{}")
            (published / "Native.PDB").write_bytes(b"prebuilt native debug symbols")
            app.write_bytes(executable("Linux", "arm64"))
            arguments.extend(["--app-publish", str(published)])
            with patch.object(sys, "argv", arguments), patch.object(package.platform, "system", return_value="Linux"), \
                 patch.object(package, "native_architecture", return_value="x64"), \
                 patch.object(package, "dependency_audit", return_value={"status": "synthetic"}), patch.object(package.subprocess, "run") as build:
                with self.assertRaises(SystemExit):
                    package.main()
                self.assertFalse((root / "dist/package").exists())
                app.write_bytes(executable("Linux", "x64"))
                self.assertEqual(package.main(), 0)
                build.assert_not_called()
            self.assertEqual((root / "dist/package/payload/app/ColorCorrect.App").read_bytes(), app.read_bytes())
            self.assertFalse((root / "dist/package/payload/app/Native.PDB").exists())
            self.assertEqual((published / "Native.PDB").read_bytes(), b"prebuilt native debug symbols")

    def test_removed_video_feature_rejected_before_publish(self):
        with patch.object(sys, "argv", ["package_native.py", "--rid", "win-x64", "--worker", "none", "--ort-runtime", "none", "--features", "video"]), \
             patch.object(package.subprocess, "run") as publish:
            with self.assertRaises(SystemExit):
                package.main()
            publish.assert_not_called()

    def test_output_inside_models_is_rejected_before_publish(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            arguments = self._inputs(root, "win-x64")
            arguments[arguments.index("--output") + 1] = str(root / "models/nested-package")
            with patch.object(sys, "argv", arguments), patch.object(package.platform, "system", return_value="Windows"), \
                 patch.object(package, "native_architecture", return_value="x64"), \
                 patch.object(package.subprocess, "run") as publish:
                with self.assertRaises(SystemExit):
                    package.main()
                publish.assert_not_called()

    def test_published_apphost_must_match_rid(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            arguments = self._inputs(root, "win-arm64")

            def publish(command, **kwargs):
                destination = Path(command[command.index("-o") + 1])
                destination.mkdir(parents=True)
                (destination / "ColorCorrect.App.exe").write_bytes(executable("Windows", "x64"))

            with patch.object(sys, "argv", arguments), patch.object(package.platform, "system", return_value="Windows"), \
                 patch.object(package, "native_architecture", return_value="arm64"), \
                 patch.object(package.subprocess, "run", side_effect=publish):
                with self.assertRaisesRegex(ValueError, "does not match"):
                    package.main()
            self.assertFalse((root / "dist/package/package-manifest.json").exists())



if __name__ == "__main__":
    unittest.main()
