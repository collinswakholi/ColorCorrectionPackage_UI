"""Documentation regressions use temporary fixtures, never a network connection."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from check_documentation import anchors, check_citation, check_demo, check_demo_manifest, check_markdown


class DocumentationChecks(unittest.TestCase):
    def test_heading_duplicates_and_unicode(self):
        self.assertEqual(anchors("# Configure FFC, GC & WB\n## Café\n## Café\n"),
                         {"configure-ffc-gc--wb", "café", "café-1"})

    def test_local_links_anchors_remote_urls_and_python_syntax(self):
        with tempfile.TemporaryDirectory(prefix="ccp-doc-check-") as directory:
            root = Path(directory)
            page = root / "README.md"
            target = root / "guide with spaces.md"
            target.write_text("# First correction\n", encoding="utf-8")
            page.write_text('[Guide](<guide with spaces.md#first-correction>)\n'
                            '[Remote](https://example.invalid/not-contacted)\n'
                            '```python\nfrom pathlib import Path\nprint(Path("image.jpg"))\n```\n', encoding="utf-8")
            self.assertEqual(check_markdown(page, root), [])
            page.write_text('[Missing](missing.png)\n[Wrong heading](guide%20with%20spaces.md#absent)\n'
                            '```python\nif True print("broken")\n```\n', encoding="utf-8")
            errors = check_markdown(page, root)
            self.assertEqual(len(errors), 3)
            self.assertTrue(any("invalid Python" in error for error in errors))
            page.write_text('[Escape](../outside.md)\n```text\n[Literal](not-a-link.md)\n```\n', encoding="utf-8")
            self.assertEqual(len(check_markdown(page, root)), 1)
            page.write_text("## 0.2.1 \u00e2\u20ac\u201d Unreleased\n\u00ce\u201dE and 65\u00c2\u00b3\n", encoding="utf-8")
            self.assertTrue(any("mojibake" in error for error in check_markdown(page, root)))

    def test_citation_and_malformed_demo_fail_explicitly(self):
        with tempfile.TemporaryDirectory(prefix="ccp-doc-media-") as directory:
            root = Path(directory)
            citation = root / "CITATION.cff"
            citation.write_text('cff-version: 1.2.0\ntype: software\nmessage: Cite us\ntitle: Studio\n'
                                'version: 0.2.0\nrepository-code: https://example.invalid\n'
                                'authors: [{family-names: Example}]\n'
                                'references: [{type: article, doi: 10.1002/ppj2.70067}]\n', encoding="utf-8")
            self.assertEqual(check_citation(citation), [])
            self.assertTrue(check_citation(citation, "0.3.0"))
            citation.write_text('title: Studio\n', encoding="utf-8")
            self.assertTrue(check_citation(citation))
            demo = root / "demo.apng"
            demo.write_bytes(b"not a PNG")
            self.assertTrue(check_demo(demo))
            demo.write_bytes(b"\x89PNG\r\n\x1a\n")
            self.assertTrue(check_demo(demo))


class StoryboardChecks(unittest.TestCase):
    def test_portable_storyboard_keeps_lf_when_git_uses_windows_checkout(self):
        # Artifact hashes must survive a Windows Git checkout with autocrlf.
        # Exercise Git's real index/filter behavior, not str.replace alone.
        with tempfile.TemporaryDirectory(prefix="ccp-demo-git-eol-") as directory:
            root = Path(directory)
            attributes = Path(__file__).resolve().parents[2] / ".gitattributes"
            (root / ".gitattributes").write_bytes(attributes.read_bytes())
            path = root / "docs/images/demo/storyboard.json"
            path.parent.mkdir(parents=True)
            path.write_bytes(b'{"frames":[]}\r\n')

            def git(*arguments):
                return subprocess.run(["git", "-c", "core.autocrlf=true", *arguments], cwd=root,
                                      check=True, capture_output=True).stdout

            git("init", "-q")
            git("add", ".gitattributes", "docs/images/demo/storyboard.json")
            index = git("show", ":docs/images/demo/storyboard.json")
            path.unlink()
            git("checkout-index", "--", "docs/images/demo/storyboard.json")
            self.assertEqual(index, b'{"frames":[]}\n')
            self.assertEqual(path.read_bytes(), index)
            self.assertEqual(hashlib.sha256(path.read_bytes()).digest(), hashlib.sha256(index).digest())

    def test_portable_storyboard_sources_timing_and_scientific_cuts(self):
        with tempfile.TemporaryDirectory(prefix="ccp-demo-story-") as directory:
            root = Path(directory)
            (root / "one.png").write_bytes(b"image checked separately by PNG scanner")
            (root / "two.png").write_bytes(b"second image")
            path = root / "storyboard.json"
            story = {"source_color_space": "srgb", "canvas": [1440, 900], "frames": [
                {"id": "one", "source": "one.png", "duration_ms": 25000},
                {"id": "two", "source": "two.png", "duration_ms": 25000}]}

            def check(value):
                path.write_text(json.dumps(value), encoding="utf-8")
                return check_demo(path)

            self.assertEqual(check(story), [])
            for source in ("../one.png", "C:/one.png", "https://example.invalid/one.png", "missing.png"):
                changed = {**story, "frames": [story["frames"][0], {**story["frames"][1], "source": source}]}
                self.assertTrue(check(changed), source)
            changed = {**story, "frames": [story["frames"][0],
                        {**story["frames"][1], "comparison": True, "transition_ms": 160}]}
            self.assertTrue(any("clean cuts" in error for error in check(changed)))
            changed = {**story, "frames": [story["frames"][0],
                        {**story["frames"][1], "id": "one", "duration_ms": 20}]}
            errors = check(changed)
            self.assertTrue(any("unique" in error for error in errors))
            self.assertTrue(any("duration" in error for error in errors))
            path.write_text('{"frames":', encoding="utf-8")
            self.assertTrue(any("invalid storyboard JSON" in error for error in check_demo(path)))


class DemoManifestChecks(unittest.TestCase):
    def fixture(self, root):
        files = {"studio-dark.png": b"primary PNG fixture", "studio-dark.gif": b"GIF fixture",
                 "stills/loaded.png": b"first still", "stills/detected.png": b"second still"}
        for name, data in files.items():
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        manifest = {
            "source_color_space": "srgb", "dimensions": [1440, 900], "total_duration_ms": 50000,
            "capture": {"future_metadata": {"description": "extra capture details are allowed"}},
            "frames": [{"id": "loaded", "duration_ms": 25000},
                       {"id": "detected", "duration_ms": 25000}],
            "artifacts": {name: {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
                          for name, data in files.items()},
        }
        return root / "demo-manifest.json", manifest

    def write(self, path, manifest):
        path.write_text(json.dumps(manifest), encoding="utf-8")

    def test_manifest_checks_actual_artifact_hashes_and_allows_capture_metadata(self):
        with tempfile.TemporaryDirectory(prefix="ccp-demo-manifest-") as directory:
            root = Path(directory)
            path, manifest = self.fixture(root)
            self.write(path, manifest)
            self.assertEqual(check_demo_manifest(path), [])
            self.assertEqual(check_demo(path), [])  # Integrated scanner dispatch.
            # Hash comparison must catch equal-length content edits, not only size.
            (root / "studio-dark.gif").write_bytes(b"GIF corrupt")
            self.assertTrue(any("SHA-256 mismatch" in error for error in check_demo(path)))
            (root / "stills/detected.png").unlink()
            self.assertTrue(any("missing artifact" in error for error in check_demo(path)))

    def test_invalid_json_and_basic_structure_fail_with_diagnostics(self):
        with tempfile.TemporaryDirectory(prefix="ccp-demo-json-") as directory:
            root = Path(directory)
            path, manifest = self.fixture(root)
            for text in ('{"frames":', '[]', '{"frames":[],"frames":[]}'):
                with self.subTest(text=text):
                    path.write_text(text, encoding="utf-8")
                    self.assertTrue(check_demo_manifest(path))
            for field, value in (("dimensions", [True, 900]), ("dimensions", [1440]),
                                 ("source_color_space", "unknown"), ("total_duration_ms", -1),
                                 ("frames", [{"id": "only", "duration_ms": 1}]),
                                 ("artifacts", {})):
                with self.subTest(field=field, value=value):
                    changed = {**manifest, field: value}
                    self.write(path, changed)
                    self.assertTrue(check_demo_manifest(path))
            changed = {**manifest, "frames": [{"id": "same", "duration_ms": 1},
                                                {"id": "same", "duration_ms": 1}]}
            self.write(path, changed)
            self.assertTrue(any("unique" in error for error in check_demo_manifest(path)))

    def test_bad_metadata_sizes_and_hashes_are_rejected(self):
        with tempfile.TemporaryDirectory(prefix="ccp-demo-hashes-") as directory:
            root = Path(directory)
            path, manifest = self.fixture(root)
            for metadata in ([], {"bytes": True, "sha256": "f" * 64},
                             {"bytes": 1, "sha256": "f" * 63},
                             {"bytes": -1, "sha256": 123}):
                with self.subTest(metadata=metadata):
                    changed = {**manifest, "artifacts": {**manifest["artifacts"], "studio-dark.png": metadata}}
                    self.write(path, changed)
                    self.assertTrue(check_demo_manifest(path))

    def test_external_and_ambiguous_artifact_paths_are_rejected(self):
        with tempfile.TemporaryDirectory(prefix="ccp-demo-paths-") as directory:
            root = Path(directory)
            path, manifest = self.fixture(root)
            metadata = manifest["artifacts"]["studio-dark.png"]
            for name in ("../outside.png", "/outside.png", "C:/outside.png", "C:outside.png",
                         "\\\\server\\share\\image.png", "stills/../../outside.png",
                         "https://example.invalid/image.png", "http://[", "stills//loaded.png",
                         "stills/./loaded.png", "stills/loaded.png:stream", "bad\x00name.png"):
                with self.subTest(name=name):
                    changed = {**manifest, "artifacts": {**manifest["artifacts"], name: metadata}}
                    self.write(path, changed)
                    self.assertTrue(any("relative path" in error for error in check_demo_manifest(path)))

    def test_symlink_cannot_escape_manifest_directory(self):
        with tempfile.TemporaryDirectory(prefix="ccp-demo-link-") as directory:
            root = Path(directory)
            path, manifest = self.fixture(root)
            outside = root / "outside.bin"
            outside.write_bytes(b"outside manifest directory")
            demo = root / "demo"
            demo.mkdir()
            link = demo / "escape.png"
            try:
                link.symlink_to(outside)
            except (OSError, NotImplementedError):
                self.skipTest("This environment cannot create the symlink fixture")
            manifest["artifacts"] = {"escape.png": {"bytes": outside.stat().st_size,
                                                      "sha256": hashlib.sha256(outside.read_bytes()).hexdigest()}}
            path = demo / "demo-manifest.json"
            self.write(path, manifest)
            self.assertTrue(any("escapes" in error for error in check_demo_manifest(path)))


if __name__ == "__main__":
    unittest.main()
