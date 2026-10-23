"""License and provenance coverage for locally patched third-party Rust crates."""
from pathlib import Path
import json
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parents[1]))
from collect_dependency_notices import collect, validate_notices
from package_support import digest


class VendorNoticeTests(unittest.TestCase):
    def _inputs(self, root):
        for name in ("engine/Cargo.toml", "engine/Cargo.lock", "app/ColorCorrect.App/ColorCorrect.App.csproj",
                     "global.json", "rust-toolchain.toml"):
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("fixture\n", encoding="utf-8")
        folder = root / "engine/vendor/third-party-0.19.0"
        (folder / "src").mkdir(parents=True)
        files = {"Cargo.toml": "[package]\nname = 'third-party'\nversion = '0.19.0'\n",
                 "LICENSE": "Copyright Māori 作者\nMIT fixture license\n", "src/lib.rs": "// upstream\n",
                 ".cargo_vcs_info.json": json.dumps({"git": {"sha1": "a" * 40}, "path_in_vcs": "third-party"})}
        for name, content in files.items():
            (folder / name).write_text(content, encoding="utf-8")
        provenance = {"schema": 1, "package": {"name": "third-party", "version": "0.19.0",
                      "registry_archive_sha256": "b" * 64, "git_commit": "a" * 40},
                      "files": {name: digest(folder / name) for name in files}}
        (folder / "upstream-sha256.json").write_text(json.dumps(provenance), encoding="utf-8")
        (folder / "src/lib.rs").write_text("// reviewed local annotation\n", encoding="utf-8")
        (folder / "CCP-PATCH.md").write_text("Local annotation; no algorithm changes.\n", encoding="utf-8")
        assets = root / "app/ColorCorrect.App/obj/project.assets.json"
        assets.parent.mkdir()
        assets.write_text(json.dumps({"packageFolders": {}}), encoding="utf-8")
        published = root / "published"
        published.mkdir()
        (published / "ColorCorrect.App.deps.json").write_text(json.dumps({"libraries": {}}), encoding="utf-8")
        metadata = {"packages": [{"name": "third-party", "version": "0.19.0", "source": None,
            "manifest_path": str(folder / "Cargo.toml"), "repository": "https://github.com/example/third-party",
            "license": "MIT", "authors": ["Māori 作者"]},
            {"name": "ccp_bridge", "version": "0.2.0", "source": None,
             "manifest_path": str(root / "engine/crates/ccp_bridge/Cargo.toml")}]}
        metadata_path = root / "metadata.json"
        metadata_path.write_text(json.dumps(metadata), encoding="utf-8")
        return published, folder, metadata_path

    def _collect(self, root, published, metadata, active=True):
        tree = "ccp_bridge v0.2.0\n" + ("third-party v0.19.0 (path)\n" if active else "")
        with patch("collect_dependency_notices.subprocess.check_output", return_value=tree):
            return collect(root, "linux-arm64", published, root / "notices", root / "cache",
                           metadata_file=metadata)

    def test_active_vendor_retains_license_patch_and_pinned_source_provenance(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            published, folder, metadata = self._inputs(root)
            report = self._collect(root, published, metadata)
            self.assertEqual([component["name"] for component in report["components"]], ["third-party"])
            component = report["components"][0]
            self.assertEqual(component["commit"], "a" * 40)
            self.assertEqual(component["corresponding_source"], "https://github.com/example/third-party/tree/" + "a" * 40)
            self.assertEqual(component["package_metadata"]["authors"], ["Māori 作者"])
            self.assertEqual(component["package_metadata"]["vendored_source"], "engine/vendor/third-party-0.19.0")
            self.assertEqual(component["package_metadata"]["upstream_provenance"]["package"]["registry_archive_sha256"], "b" * 64)
            notices = {notice["original_name"]: notice for notice in component["notices"]}
            self.assertEqual(set(notices), {"LICENSE", "CCP-PATCH.md"})
            for name, notice in notices.items():
                self.assertEqual(notice["provenance"], "vendored source")
                self.assertEqual((root / "notices" / notice["path"]).read_bytes(), (folder / name).read_bytes())
            for path in folder.rglob("*"):
                if path.is_file():
                    self.assertEqual(report["source_hashes"][path.relative_to(root).as_posix()], digest(path))
            self.assertIn("engine/Cargo.toml", report["source_hashes"])
            validate_notices(root / "notices", "linux-arm64", root, published)

    def test_changed_vendor_source_invalidates_collected_notices(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            published, folder, metadata = self._inputs(root)
            self._collect(root, published, metadata)
            (folder / "src/lib.rs").write_text("// changed after collection\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "different source: engine/vendor/"):
                validate_notices(root / "notices", "linux-arm64", root, published)

    def test_inactive_vendor_is_not_part_of_the_distributed_dependency_graph(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            published, folder, metadata = self._inputs(root)
            (folder / "upstream-sha256.json").unlink()
            report = self._collect(root, published, metadata, active=False)
            self.assertEqual(report["components"], [])
            self.assertFalse(any(name.startswith("engine/vendor/") for name in report["source_hashes"]))

    def test_vendor_cannot_silently_drop_its_upstream_provenance_or_local_inventory(self):
        cases = ("commit", "escape", "missing", "untracked", "license", "no-license")
        for case in cases:
            with self.subTest(case=case), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary).resolve()
                published, folder, metadata = self._inputs(root)
                provenance_file = folder / "upstream-sha256.json"
                provenance = json.loads(provenance_file.read_text(encoding="utf-8"))
                if case == "commit":
                    provenance["package"]["git_commit"] = "c" * 40
                elif case == "escape":
                    provenance["files"]["../outside/LICENSE"] = "d" * 64
                elif case == "missing":
                    (folder / "CCP-PATCH.md").unlink()
                elif case == "license":
                    (folder / "LICENSE").write_text("removed upstream terms\n", encoding="utf-8")
                elif case == "no-license":
                    (folder / "LICENSE").unlink()
                    del provenance["files"]["LICENSE"]
                else:
                    (folder / "unreviewed.rs").write_text("unexpected\n", encoding="utf-8")
                provenance_file.write_text(json.dumps(provenance), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "vendored"):
                    self._collect(root, published, metadata)

    def test_vendor_path_cannot_alias_a_package_outside_the_vendor_directory(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            published, _, metadata_file = self._inputs(root)
            metadata = json.loads(metadata_file.read_text(encoding="utf-8"))
            metadata["packages"][0]["manifest_path"] = str(root / "engine/vendor/../outside/Cargo.toml")
            metadata_file.write_text(json.dumps(metadata), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "contained without links"):
                self._collect(root, published, metadata_file)


if __name__ == "__main__":
    unittest.main()
