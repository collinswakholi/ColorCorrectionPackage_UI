"""Execute released tutorials in fresh kernels; outputs stay in a temporary folder.

Requires nbformat, nbclient and ipykernel (QA only, not SDK dependencies).
"""
from pathlib import Path
import argparse
import os
import tempfile
import time

import nbformat
from nbclient import NotebookClient

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[2])
args = parser.parse_args()
root = args.root.resolve()
os.environ['COLORCORRECT_ROOT'] = str(root)
out = Path(tempfile.mkdtemp(prefix="colorcorrect-notebook-qa-"))
print("Executed notebook copies:", out, flush=True)
for path in sorted((root / "notebooks").glob("*.ipynb")):
    notebook = nbformat.read(path, as_version=4)
    nbformat.validate(notebook)
    start = time.perf_counter()
    NotebookClient(notebook, timeout=900, kernel_name="python3",
                   resources={"metadata": {"path": str(root)}}).execute()
    nbformat.validate(notebook)
    nbformat.write(notebook, out / path.name)
    print(path.name, "PASS", round(time.perf_counter() - start, 2), "seconds", flush=True)
    for cell in notebook.cells:
        for output in cell.get("outputs", []):
            if output.get("output_type") == "stream":
                print(output.get("text", "").rstrip(), flush=True)
