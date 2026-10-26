"""Exercise real Git comparisons and the required aggregate without GitHub access."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from classify_ci_changes import changed_paths, classify_event, classify_paths, lightweight_path
from check_ci_results import failures


class ChangeClassification(unittest.TestCase):
    def test_readme_demo_and_bundled_documentation(self):
        for paths in (["README.md"], ["docs/images/demo/studio-dark.png"], ["docs/images/demo/demo-manifest.json"],
                      ["docs/images/demo/storyboard.json"],
                      ["README.md", "docs/images/demo/studio-dark.gif", "docs/images/demo/README.md"]):
            self.assertFalse(classify_paths(paths)["native"])
        for path in ("api/README.md", "docs/api/README.md", "docs/user-guide.md", "docs/images/workflow.svg",
                     "docs/images/demo/runner.py", "docs/images/demo/unknown.json", "notebooks/tutorial.ipynb", "unknown/new-file.txt",
                     "/README.md", "../README.md", "docs\\images\\demo\\test.png"):
            self.assertTrue(classify_paths(["README.md", path])["native"], path)
        for paths in (None, [], ["README.md", "engine/src/main.rs"]):
            self.assertTrue(classify_paths(paths)["native"])

    def test_tags_manual_and_missing_history_always_run_native(self):
        for event, reference in (("workflow_dispatch", "refs/heads/main"), ("push", "refs/tags/v0.2.1"),
                                 ("unknown", "refs/heads/main"), ("push", "refs/heads/main")):
            self.assertTrue(classify_event(Path.cwd(), event, reference, {})["native"])

    def test_git_add_delete_rename_and_pull_request_merge_base(self):
        with tempfile.TemporaryDirectory(prefix="ccp-ci-changes-") as directory:
            root = Path(directory)

            def git(*args):
                return subprocess.run(["git", "-c", "user.name=CI Contract", "-c", "user.email=ci@example.invalid",
                                       *args], cwd=root, check=True, capture_output=True, text=True).stdout.strip()

            def commit(message):
                git("add", "--all")
                git("commit", "-qm", message)
                return git("rev-parse", "HEAD")

            git("init", "-q")
            (root / "README.md").write_text("Read me\n")
            (root / "engine.rs").write_text("// native source preserved in rename\n")
            initial = commit("initial")
            (root / "README.md").write_text("Updated read me\n")
            readme = commit("readme")
            result = classify_event(root, "push", "refs/heads/main", {"before": initial, "after": readme})
            self.assertFalse(result["native"])
            pr = {"pull_request": {"base": {"sha": initial}, "head": {"sha": readme}}}
            self.assertFalse(classify_event(root, "pull_request", "refs/pull/1/merge", pr)["native"])
            # Inspect old and new names: source cannot evade validation by moving into demo/.
            demo = root / "docs/images/demo"
            demo.mkdir(parents=True)
            (root / "engine.rs").rename(demo / "source.md")
            renamed = commit("rename into documentation")
            paths = changed_paths(root, readme, renamed)
            self.assertEqual(set(paths), {"engine.rs", "docs/images/demo/source.md"})
            self.assertTrue(classify_paths(paths)["native"])
            (demo / "source.md").unlink()
            deleted = commit("remove demo")
            self.assertFalse(classify_paths(changed_paths(root, renamed, deleted))["native"])
            (demo / "a new frame.png").write_bytes(b"PNG fixture")
            added = commit("add frame with spaces")
            self.assertFalse(classify_paths(changed_paths(root, deleted, added))["native"])
            invalid = {"before": "0" * 40, "after": added}
            self.assertTrue(classify_event(root, "push", "refs/heads/main", invalid)["native"])
            invalid["before"] = "f" * 40
            self.assertTrue(classify_event(root, "push", "refs/heads/main", invalid)["native"])


class AggregateResult(unittest.TestCase):
    def jobs(self, native):
        return {"changes": {"result": "success", "outputs": {"native": str(native).lower()}},
                "documentation": {"result": "success"},
                "packages": {"result": "success" if native else "skipped"},
                "macos_source": {"result": "success" if native else "skipped"}}

    def test_only_complete_applicable_jobs_pass(self):
        for native in (True, False):
            self.assertEqual(failures(self.jobs(native)), [])
            for name in ("changes", "documentation", "packages", "macos_source"):
                for result in ("failure", "cancelled"):
                    jobs = self.jobs(native)
                    jobs[name]["result"] = result
                    self.assertTrue(failures(jobs), (native, name, result))
        jobs = self.jobs(True)
        jobs["packages"]["result"] = "skipped"
        self.assertTrue(failures(jobs))
        jobs = self.jobs(False)
        jobs["changes"]["outputs"].clear()
        self.assertTrue(failures(jobs))
        self.assertTrue(failures({}))


if __name__ == "__main__":
    unittest.main()
