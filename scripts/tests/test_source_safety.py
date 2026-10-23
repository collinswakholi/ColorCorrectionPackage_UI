"""Safety contracts for current-source export and cache inventory."""
from pathlib import Path
import json
import os
import shutil
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import subprocess

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import cleanup_local
import export_source
from package_support import source_identity, digest


class SourceSafetyTests(unittest.TestCase):
    def test_source_identity_records_actual_git_state_without_inventing_archive_commit(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            manifest = root / "source-manifest.json"
            manifest.write_text('{"kind":"source archive"}')
            with patch("package_support.subprocess.run") as git:
                self.assertEqual(source_identity(root), {"git_commit": None, "working_tree_dirty": None,
                                                         "source_manifest_sha256": digest(manifest)})
                git.assert_not_called()
            (root / ".git").mkdir()
            replies = [subprocess.CompletedProcess([], 0, str(root) + "\n"),
                       subprocess.CompletedProcess([], 0, "a" * 40 + "\n"),
                       subprocess.CompletedProcess([], 0, " M app/source.cs\n?? api/new.py\n")]
            with patch("package_support.subprocess.run", side_effect=replies):
                identity = source_identity(root)
            self.assertEqual(identity["git_commit"], "a" * 40)
            self.assertTrue(identity["working_tree_dirty"])
            with patch("package_support.subprocess.run", return_value=subprocess.CompletedProcess([], 0, str(root.parent) + "\n")):
                self.assertIsNone(source_identity(root)["git_commit"])

    @staticmethod
    def _minimal_models(root):
        (root / "models").mkdir()
        (root / "models/manifest.json").write_text(json.dumps({"files": []}))
        (root / "models/NOTICE.md").write_text("notice")
        (root / "models/LICENSE-AGPL-3.0.txt").write_text("license")

    def test_posix_scripts_require_lf_before_git_normalization(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            self._minimal_models(root)
            (root / "scripts").mkdir()
            (root / "scripts/run.sh").write_bytes(b"#!/bin/sh\r\nset -eu\r\n")
            with patch.object(export_source, "REQUIRED", set()):
                with self.assertRaisesRegex(ValueError, "requires LF"):
                    export_source.inventory(root)

    @unittest.skipUnless(shutil.which("git"), "Git is required to verify checkout attributes")
    def test_linux_installer_shell_templates_survive_windows_checkout(self):
        source = Path(__file__).resolve().parents[2]
        scripts = sorted(path for path in (source / "installer/linux").iterdir()
                         if path.is_file() and path.read_bytes().startswith(b"#!"))
        self.assertTrue(scripts, "No actual Linux installer shell templates were found")
        # Exercise Git's real checkout conversion in a disposable repository.
        # No index, configuration, or working file in the source repository changes.
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            environment = dict(os.environ, GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull)

            def git(*arguments):
                return subprocess.check_output(
                    ["git", "-C", str(root), "-c", "core.autocrlf=true", "-c", "core.eol=crlf", *arguments],
                    env=environment, stderr=subprocess.STDOUT)

            git("init", "--quiet")
            shutil.copy2(source / ".gitattributes", root / ".gitattributes")
            paths = [path.relative_to(source).as_posix() for path in scripts]
            for path, relative in zip(scripts, paths):
                destination = root / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(path, destination)
            fields = git("check-attr", "-z", "text", "eol", "--", *paths).decode().split("\0")[:-1]
            attributes = {(fields[index], fields[index + 1]): fields[index + 2]
                          for index in range(0, len(fields), 3)}
            for relative in paths:
                with self.subTest(script=relative):
                    self.assertEqual(attributes[relative, "text"], "set")
                    self.assertEqual(attributes[relative, "eol"], "lf")
            git("add", "--", ".gitattributes", *paths)
            git("checkout-index", "--all", "--force")
            for relative in paths:
                with self.subTest(checked_out_script=relative):
                    contents = (root / relative).read_bytes()
                    self.assertNotIn(b"\r\n", contents)
                    self.assertTrue(contents.startswith(b"#!/bin/sh\n"))

    def test_extensionless_installer_scripts_are_scanned_for_private_content(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            self._minimal_models(root)
            (root / "installer/linux").mkdir(parents=True)
            marker = b"-----BEGIN " + b"PRIVATE KEY-----"
            (root / "installer/linux/launch").write_bytes(b"#!/bin/sh\n# " + marker + b"\n")
            with patch.object(export_source, "REQUIRED", set()):
                with self.assertRaisesRegex(ValueError, "private path/key"):
                    export_source.inventory(root)

    def test_windows_reparse_point_is_rejected_without_path_is_junction(self):
        metadata = SimpleNamespace(st_mode=0o40755, st_file_attributes=0x400)
        with patch.object(Path, "lstat", return_value=metadata):
            self.assertTrue(export_source.is_link(Path("junction")))
            self.assertTrue(cleanup_local.linked(Path("junction")))

    def test_rust_source_bin_is_not_a_cache(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            crate = root / "engine/crates/test"
            (crate / "src/bin").mkdir(parents=True)
            (crate / "Cargo.toml").write_text("[package]\nname='test'\n")
            (crate / "src/bin/main.rs").write_text("fn main() {}")
            (crate / "target/debug").mkdir(parents=True)
            (crate / "target/debug/output").write_bytes(b"generated")
            report = cleanup_local.inventory(root)
            self.assertEqual([item["path"] for item in report["candidates"]], ["engine/crates/test/target"])
            self.assertTrue((crate / "src/bin/main.rs").exists())

    def test_dotnet_bin_requires_project_manifest(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            for name in ("app/Actual/bin", "app/Source/bin"):
                (root / name).mkdir(parents=True)
            (root / "app/Actual/App.csproj").write_text("<Project/>")
            self.assertEqual([item["path"] for item in cleanup_local.inventory(root)["candidates"]], ["app/Actual/bin"])

    def test_test_results_and_nested_caches_remain_evidence(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            evidence = root / "app/Actual/TestResults"
            (evidence / "__pycache__").mkdir(parents=True)
            (evidence.parent / "App.csproj").write_text("<Project/>")
            (evidence / "observed.png").write_bytes(b"unique capture")
            self.assertEqual(cleanup_local.inventory(root)["candidates"], [])
            self.assertTrue((evidence / "observed.png").exists())

    def test_inventory_has_no_delete_option(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            (root / "api/__pycache__").mkdir(parents=True)
            sentinel = root / "api/__pycache__/cache.pyc"
            sentinel.write_bytes(b"cache")
            self.assertEqual(cleanup_local.inventory(root)["action"], "dry-run-only")
            self.assertTrue(sentinel.exists())

    def test_export_rejects_junction_or_symlink_tree(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            (root / "api").mkdir()
            with patch.object(export_source, "is_link", side_effect=lambda path: path == root / "api"):
                with self.assertRaisesRegex(ValueError, "link"):
                    export_source.inventory(root)

    def test_export_includes_new_untracked_source_and_ignores_cache(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            (root / "api/__pycache__").mkdir(parents=True)
            (root / "api/new_untracked.py").write_text("print('new')")
            (root / "engine/crates/example/src/bin").mkdir(parents=True)
            (root / "engine/crates/example/src/bin/cli.rs").write_text("fn main() {}")
            (root / "api/__pycache__/ignored.pyc").write_bytes(b"cache")
            (root / "models").mkdir()
            (root / "models/manifest.json").write_text(json.dumps({"files": []}))
            (root / "models/NOTICE.md").write_text("notice")
            (root / "models/LICENSE-AGPL-3.0.txt").write_text("license")
            with patch.object(export_source, "REQUIRED", set()):
                paths = {item["path"] for item in export_source.inventory(root)}
            self.assertIn("api/new_untracked.py", paths)
            self.assertIn("engine/crates/example/src/bin/cli.rs", paths)
            self.assertFalse(any("__pycache__" in path for path in paths))


if __name__ == "__main__":
    unittest.main()
