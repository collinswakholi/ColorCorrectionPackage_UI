"""Run public tutorials with an installed SDK in fresh, explicitly bound kernels.

QA dependencies are in notebooks/requirements-qa.txt. Copy this runner and the
notebooks into a guest; the source checkout is not required or imported.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile
import time

import nbformat
from nbclient import NotebookClient
from jupyter_client import KernelManager


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--notebooks-dir", type=Path, required=True)
    parser.add_argument("--root", type=Path, required=True, help="Installed/extracted application root")
    parser.add_argument("--samples", type=Path, required=True, help="Public photograph corpus directory")
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--reference-csv", type=Path, required=True, help="Complete Classic Lab D50 CSV")
    args = parser.parse_args()
    for key in ("notebooks_dir", "root", "samples"):
        value = getattr(args, key).resolve()
        if not value.is_dir():
            parser.error(f"Missing directory: {value}")
        setattr(args, key, value)
    if not args.reference_csv.is_file():
        parser.error("--reference-csv must name an existing Classic Lab D50 CSV")
    args.output_dir.mkdir(parents=True, exist_ok=True)
    out = Path(tempfile.mkdtemp(prefix="notebook-qa-", dir=args.output_dir.resolve()))
    cwd = out / "neutral-working-directory"
    cwd.mkdir()
    native_modules = ("colorcorrect", "colorcorrect_sdk", "colorcorrect_pipeline")
    module_paths = {}
    for name in native_modules:
        module = __import__(name)
        path = Path(module.__file__).resolve()
        if not path.is_relative_to(Path(sys.prefix).resolve()):
            raise RuntimeError(f"Install the SDK wheel in this environment: {name} resolved to {path}")
        module_paths[name] = str(path)
    environment = dict(os.environ)
    environment.pop("PYTHONPATH", None)
    environment.update(COLORCORRECT_APP_ROOT=str(args.root),
                       COLORCORRECT_SAMPLES=str(args.samples),
                       COLORCORRECT_OUTPUT_DIR=str(out),
                       COLORCORRECT_REFERENCE_CSV=str(args.reference_csv.resolve()))
    report = {"ok": False, "interpreter": sys.executable, "prefix": sys.prefix,
              "modules": module_paths, "application": str(args.root),
              "samples": str(args.samples), "neutral_cwd": str(cwd), "notebooks": []}
    print("Executed notebook copies:", out, flush=True)
    paths = [args.notebooks_dir / name for name in
             ("01_headless_workflow.ipynb", "02_compare_fit_methods.ipynb")]
    try:
        for path in paths:
            notebook = nbformat.read(path, as_version=4)
            nbformat.validate(notebook)
            started = time.perf_counter()
            # Mutate only this KernelSpec instance. No persistent kernel is installed,
            # and another environment's default `python3` cannot be borrowed.
            manager = KernelManager(kernel_name="python3")
            manager.kernel_spec.argv = [sys.executable, "-m", "ipykernel_launcher", "-f", "{connection_file}"]
            manager.kernel_spec.env = environment
            row = {"name": path.name, "source_sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                   "kernel_interpreter": sys.executable, "ok": False}
            report["notebooks"].append(row)
            try:
                NotebookClient(notebook, timeout=900, km=manager,
                               resources={"metadata": {"path": str(cwd)}}).execute(env=environment, cleanup_kc=True)
                nbformat.validate(notebook)
                row["ok"] = True
            finally:
                nbformat.write(notebook, out / path.name)
                row["seconds"] = round(time.perf_counter() - started, 3)
            print(path.name, "PASS", row["seconds"], "seconds", flush=True)
            for cell in notebook.cells:
                for output in cell.get("outputs", []):
                    if output.get("output_type") == "stream":
                        print(output.get("text", "").rstrip(), flush=True)
        report["ok"] = all(row["ok"] for row in report["notebooks"])
    finally:
        (out / "verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print("Verification:", out / "verification.json", flush=True)


if __name__ == "__main__":
    main()
