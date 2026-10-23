"""Installer/bootstrap boundaries; no installers or target binaries are executed."""
from pathlib import Path
import importlib.util
import json
import os
import sys
import tempfile
import unittest
import subprocess
from unittest.mock import patch

SCRIPTS = Path(__file__).parents[1]
sys.path.insert(0, str(SCRIPTS))
from package_support import check_runtime_manifest, check_model_manifest, pe_details, dependency_audit, digest
from native_ci_acceptance import clean_environment
from package_wsl_addon import validate_addon
from collect_dependency_notices import collect, validate_notices
import test_package_native as fixtures


class ReleaseContracts(unittest.TestCase):
    def test_runtime_missing_provider_or_notice_is_rejected(self):
        helper = fixtures.NativePackageTests()
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            helper._inputs(root, "win-x64")
            check_runtime_manifest(root / "runtime", "win-x64", "Windows")
            (root / "runtime/onnxruntime_providers_webgpu.dll").unlink()
            with self.assertRaisesRegex(ValueError, "Missing required"):
                check_runtime_manifest(root / "runtime", "win-x64", "Windows")

    def test_model_changes_require_manifest_update(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            fixtures.NativePackageTests()._inputs(root, "linux-x64")
            check_model_manifest(root)
            (root / "models/opt2/best_assemble_fp32_1280.onnx").write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "hash mismatch"):
                check_model_manifest(root)

    def test_clean_guest_environment_does_not_inherit_development_or_gpu_paths(self):
        unsafe = {"CCP_ENGINE_EXE": "dev.exe", "CCP_NEW_UI_ROOT": "source", "PYTHONPATH": "source/api",
                  "LD_LIBRARY_PATH": "dev/torch", "VK_DRIVER_FILES": "dev/icd", "WGPU_ALLOW_UNDERLYING_NONCOMPLIANT_ADAPTER": "1",
                  "ORT_DYLIB_PATH": "dev/runtime", "PATH": "developer/toolchain", "HOME": "/home/guest",
                  "LD_PRELOAD": "dev/libhook.so", "LD_AUDIT": "dev/libaudit.so", "DOTNET_STARTUP_HOOKS": "dev/hook.dll",
                  "DOTNET_ADDITIONAL_DEPS": "dev/deps", "DOTNET_SHARED_STORE": "dev/store", "COMPlus_GCStress": "1"}
        with patch.dict(os.environ, unsafe, clear=True):
            result = clean_environment(Path("/installed"), "linux-x64")
        self.assertEqual(result["LD_LIBRARY_PATH"], str(Path("/installed")))
        self.assertEqual(result["CCP_ENGINE_EXE"], str(Path("/installed") / "ccp-engine-launch"))
        self.assertEqual(result["CCP_WSL_GPU_ADDON"], "0")
        for key in ("PYTHONPATH", "VK_DRIVER_FILES", "WGPU_ALLOW_UNDERLYING_NONCOMPLIANT_ADAPTER", "LD_PRELOAD", "LD_AUDIT",
                    "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "COMPlus_GCStress"):
            self.assertNotIn(key, result)
        self.assertNotIn("developer", result["PATH"])

    def test_macos_packaging_is_not_a_distribution_target(self):
        import package_native
        self.assertIn("osx-arm64", package_native.TARGETS)
        self.assertNotIn("osx-arm64", package_native.PACKAGE_TARGETS)

    def test_windows_clean_environment_accepts_uppercase_systemroot(self):
        unsafe = {"SYSTEMROOT": "C:/Windows", "Path": "dev/bin", "PSMODULEPATH": "dev/modules", "USERPROFILE": "C:/Users/guest"}
        with patch.dict(os.environ, unsafe, clear=True):
            result = clean_environment(Path("C:/installed"), "win-x64")
        self.assertNotIn("dev", result["PATH"])
        self.assertNotIn("Path", result)
        self.assertEqual(sum(key.upper() == "PSMODULEPATH" for key in result), 1)
        self.assertIn("WindowsPowerShell", result["PSModulePath"])

    def test_wsl_addon_rejects_wrong_architecture(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "manifest.json").write_text(json.dumps({"rid": "linux-arm64", "mesa_version": "25.2.8"}))
            with self.assertRaisesRegex(ValueError, "WSL x64"):
                validate_addon(root, "linux-arm64")

    def test_linux_gui_launcher_does_not_enable_experimental_driver(self):
        text = (SCRIPTS.parents[0] / "installer/linux/ColorCorrect-Studio").read_text()
        self.assertNotIn("VK_DRIVER_FILES", text)
        self.assertNotIn("WGPU_ALLOW", text)
        self.assertIn("ccp-engine-launch", text)

    def test_minimal_pe_does_not_require_loader_execution(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "header"
            path.write_bytes(b"MZ")
            self.assertEqual(pe_details(path), (False, []))

    def test_only_the_known_optional_legacy_tracing_dependency_may_be_missing(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            library = root / "libcoreclrtraceptprovider.so"
            library.write_bytes(b"\x7fELF")
            result = subprocess.CompletedProcess([], 0, "liblttng-ust.so.0 => not found\n", "")
            with patch("package_support.subprocess.run", return_value=result):
                report = dependency_audit(root, "Linux", lambda *_: None, ("Linux", "x64"))
                self.assertEqual(len(report["optional_diagnostics"]), 1)
                library.rename(root / "libSkiaSharp.so")
                with self.assertRaisesRegex(ValueError, "Unresolved"):
                    dependency_audit(root, "Linux", lambda *_: None, ("Linux", "x64"))

    def test_wsl_addon_requires_integrity_and_relocation(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "lib").mkdir(); (root / "icd").mkdir()
            (root / "lib/libvulkan_dzn.so").write_bytes(b"driver")
            icd = root / "icd/dzn_icd.json"
            icd.write_text(json.dumps({"ICD": {"library_path": "../lib/libvulkan_dzn.so"}}))
            manifest = {"rid": "linux-x64", "mesa_version": "25.2.8", "experimental": True,
                        "sha256": {name: digest(root / name) for name in ("lib/libvulkan_dzn.so", "icd/dzn_icd.json")}}
            (root / "manifest.json").write_text(json.dumps(manifest))
            with patch("package_native.require_architecture"):
                validate_addon(root, "linux-x64")
                extra = root / "lib/unreviewed.so"
                extra.write_bytes(b"unreviewed")
                with self.assertRaisesRegex(ValueError, "Untracked"):
                    validate_addon(root, "linux-x64")
                extra.unlink()
                icd.write_text(json.dumps({"ICD": {"library_path": "/developer/prefix/libvulkan_dzn.so"}}))
                with self.assertRaisesRegex(ValueError, "checksum"):
                    validate_addon(root, "linux-x64")
                manifest["sha256"]["icd/dzn_icd.json"] = digest(icd)
                (root / "manifest.json").write_text(json.dumps(manifest))
                with self.assertRaisesRegex(ValueError, "relocatable"):
                    validate_addon(root, "linux-x64")

    def test_notice_inventory_rejects_changed_source_deps_and_extra_files(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            notices = root / "notices"; notices.mkdir()
            published = root / "published"; published.mkdir()
            source = root / "project.csproj"; source.write_text("original")
            deps = published / "ColorCorrect.App.deps.json"; deps.write_text("original deps")
            license_file = notices / "LICENSE.txt"; license_file.write_text("license")
            report = {"rid": "win-x64", "unresolved": [], "source_hashes": {"project.csproj": digest(source)},
                      "published_deps_sha256": digest(deps), "sha256": {"LICENSE.txt": digest(license_file)}}
            (notices / "manifest.json").write_text(json.dumps(report))
            validate_notices(notices, "win-x64", root, published)
            source.write_text("changed")
            with self.assertRaisesRegex(ValueError, "different source"):
                validate_notices(notices, "win-x64", root, published)
            source.write_text("original"); deps.write_text("changed")
            with self.assertRaisesRegex(ValueError, "published application"):
                validate_notices(notices, "win-x64", root, published)
            deps.write_text("original deps"); (notices / "unexpected.dll").write_bytes(b"untracked")
            with self.assertRaisesRegex(ValueError, "Untracked"):
                validate_notices(notices, "win-x64", root, published)

    @staticmethod
    def _notice_inputs(root):
        """One restored crate with non-ASCII authors and a Unicode package path."""
        for name in ("engine/Cargo.lock", "app/ColorCorrect.App/ColorCorrect.App.csproj",
                     "global.json", "rust-toolchain.toml"):
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("fixture", encoding="utf-8")
        crate = root / "restored-ā-依存"
        crate.mkdir()
        (crate / "LICENSE").write_text("Copyright Māori 作者\nFixture license\n", encoding="utf-8")
        assets = root / "app/ColorCorrect.App/obj/project.assets.json"
        assets.parent.mkdir()
        assets.write_text(json.dumps({"packageFolders": {}}), encoding="utf-8")
        published = root / "published"
        published.mkdir()
        (published / "ColorCorrect.App.deps.json").write_text(json.dumps({"libraries": {}}), encoding="utf-8")
        metadata = {"packages": [{"name": "fixture", "version": "1.0.0", "license": "MIT",
            "source": "registry+https://example.invalid/index", "manifest_path": str(crate / "Cargo.toml"),
            "authors": ["Māori 作者"]}]}
        return published, json.dumps(metadata, ensure_ascii=False).encode("utf-8"), f"fixture v1.0.0 ({crate})\n".encode("utf-8")

    def test_notice_tool_output_is_utf8_under_windows_legacy_default(self):
        real_check_output = subprocess.check_output
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            published, metadata, tree = self._notice_inputs(root)

            def cargo_output(command, **kwargs):
                data = metadata if command[1] == "metadata" else tree
                # Keep actual subprocess text decoding, but simulate the Windows
                # cp1252 default regardless of the host running this regression.
                return real_check_output([sys.executable, "-c",
                    "import sys; sys.stdout.buffer.write(bytes.fromhex('" + data.hex() + "'))"], **kwargs)

            with patch("collect_dependency_notices.subprocess.check_output", side_effect=cargo_output), \
                 patch("subprocess._text_encoding", return_value="cp1252"):
                report = collect(root, "win-x64", published, root / "notices", root / "cache")
            self.assertEqual(len(report["components"]), 1)
            component = report["components"][0]
            self.assertEqual(component["package_metadata"]["authors"], ["Māori 作者"])
            notice = root / "notices" / component["notices"][0]["path"]
            self.assertEqual(notice.read_text(encoding="utf-8"), "Copyright Māori 作者\nFixture license\n")
            validate_notices(root / "notices", "win-x64", root, published)

    def test_notice_tool_output_rejects_invalid_utf8_without_replacement(self):
        real_check_output = subprocess.check_output
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            published, _, _ = self._notice_inputs(root)

            def malformed_output(command, **kwargs):
                return real_check_output([sys.executable, "-c",
                    "import sys; sys.stdout.buffer.write(b'{\"packages\":[],\"invalid\":\"\\xff\"}')"], **kwargs)

            with patch("collect_dependency_notices.subprocess.check_output", side_effect=malformed_output), \
                 patch("subprocess._text_encoding", return_value="cp1252"), \
                 self.assertRaises(UnicodeDecodeError):
                collect(root, "win-x64", published, root / "notices", root / "cache")


if __name__ == "__main__":
    unittest.main()
