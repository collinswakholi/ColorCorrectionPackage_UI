"""Documentation regressions use temporary fixtures, never a network connection."""
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from check_documentation import anchors, check_citation, check_demo, check_markdown


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
            # Reproduce mismatched resolved/unresolved temp roots without needing
            # privileged symlink creation on Windows. Both names address one root.
            (root / "alias").mkdir()
            alias = root / "alias" / ".."
            self.assertEqual(check_markdown(alias / "README.md", alias), [])
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


if __name__ == "__main__":
    unittest.main()
