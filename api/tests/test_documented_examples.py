"""Documentation examples must use real public constructors and editable configs.

These host component tests do not launch a worker or claim native acceptance.
The runnable examples/notebooks execute separately in installed guest environments.
"""
import ast
from dataclasses import FrozenInstanceError, replace
import importlib.util
import inspect
import json
from pathlib import Path
import re
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "api"))
from colorcorrect import (
    ApplyOptions, CalibrationOptions, Config, DetectionOptions, FFCOptions,
    NeuralNetworkOptions,
)

PUBLIC_OPTIONS = {cls.__name__: cls for cls in
                  (ApplyOptions, CalibrationOptions, Config, DetectionOptions,
                   FFCOptions, NeuralNetworkOptions)}


def sources():
    for path in (ROOT / "README.md", ROOT / "api/README.md", ROOT / "docs/api/README.md"):
        for index, source in enumerate(re.findall(r"```python\n(.*?)```", path.read_text(encoding="utf-8"), re.S)):
            yield f"{path.name}:example{index}", source
    for path in sorted((ROOT / "notebooks").glob("*.ipynb")):
        notebook = json.loads(path.read_text(encoding="utf-8"))
        for index, cell in enumerate(notebook["cells"]):
            if cell["cell_type"] == "code":
                yield f"{path.name}:cell{index}", "".join(cell["source"])


class DocumentedConfigurationTests(unittest.TestCase):
    def test_option_examples_bind_to_current_public_signatures(self):
        count = 0
        for label, source in sources():
            with self.subTest(source=label):
                tree = ast.parse(source, filename=label)
                for node in ast.walk(tree):
                    if not isinstance(node, ast.Call) or not isinstance(node.func, ast.Name):
                        continue
                    cls = PUBLIC_OPTIONS.get(node.func.id)
                    if cls is None or any(isinstance(arg, ast.Starred) for arg in node.args) or any(key.arg is None for key in node.keywords):
                        continue
                    inspect.signature(cls).bind(*([object()] * len(node.args)),
                                                **{key.arg: object() for key in node.keywords})
                    count += 1
        self.assertGreaterEqual(count, 20, "Check the actual configuration examples, not only prose")

    def test_editable_standalone_configs_validate_without_starting_native_work(self):
        path = ROOT / "api/examples/configured_pipeline.py"
        spec = importlib.util.spec_from_file_location("documented_pipeline", path)
        example = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(example)
        config = example.tutorial_config(Path("tutorial-output"))
        self.assertTrue(config.do_ffc)
        changed = replace(config, do_gc=False, do_wb=False)
        effective = changed.calibration_options().to_dict()
        self.assertFalse(effective["do_gc"])
        self.assertFalse(effective["do_wb"])
        self.assertTrue(config.do_gc, "Editing must not mutate a previous run's configuration")
        with self.assertRaises(FrozenInstanceError):
            config.do_gc = False
        methods = example.alternative_configs(config)
        self.assertEqual(set(methods), {"ccm", "linear", "rpcc2", "rpcc3", "pls", "nn"})
        for name, candidate in methods.items():
            with self.subTest(method=name):
                self.assertEqual(candidate.calibration_options().to_dict()["model"], name)

    def test_notebooks_teach_installed_sdk_without_hidden_outputs(self):
        for path in sorted((ROOT / "notebooks").glob("*.ipynb")):
            notebook = json.loads(path.read_text(encoding="utf-8"))
            for cell in notebook["cells"]:
                if cell["cell_type"] != "code":
                    continue
                self.assertIsNone(cell["execution_count"])
                self.assertEqual(cell["outputs"], [])
                for node in ast.walk(ast.parse("".join(cell["source"]))):
                    if isinstance(node, ast.ImportFrom) and node.module == "colorcorrect":
                        self.assertNotIn("Engine", [name.name for name in node.names])
                    if isinstance(node, ast.Attribute) and node.attr == "path" and isinstance(node.value, ast.Name):
                        self.assertNotEqual(node.value.id, "sys", "Tutorials must not borrow a checkout through sys.path")


if __name__ == "__main__":
    unittest.main()
