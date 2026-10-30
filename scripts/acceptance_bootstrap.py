"""Installed SDK/worker smoke without requiring a GPU; never certifies a full workflow."""
from pathlib import Path
import argparse
import importlib.metadata
import json
import os
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--fixtures", type=Path, required=True)
    args = parser.parse_args()
    package = args.package.resolve(strict=True)
    args.output.mkdir(parents=True, exist_ok=False)
    os.chdir(args.output)
    import colorcorrect
    from colorcorrect import Engine, Client
    module = Path(colorcorrect.__file__).resolve()
    if not module.is_relative_to(Path(sys.prefix).resolve()):
        raise RuntimeError("SDK was imported from outside the clean virtual environment")
    report = {"sdk_version": importlib.metadata.version("colorcorrect-studio"), "sdk_module": str(module),
              "scope": "installed SDK/worker smoke only", "full_workflow": "checked separately by acceptance_cpu_workflow.py"}
    with Engine(root=package, policy="cpu") as engine:
        if not engine.executable.resolve().is_relative_to(package):
            raise RuntimeError("Worker escaped the installed package")
        report["executable"] = str(engine.executable)
        report["devices"] = engine.request("devices")
        report["capabilities"] = engine.ready
        assert engine.request("video", check=False)["ok"] is False
        pid = engine._process.pid
        if sys.platform.startswith("linux"):
            mapped = Path(f"/proc/{pid}/maps").read_text()
            report["loaded_library_paths"] = sorted({line.split()[-1] for line in mapped.splitlines() if "/" in line.split()[-1]})
        elif os.name == "nt":
            command = f"(Get-Process -Id {pid}).Modules | Select-Object -ExpandProperty FileName | ConvertTo-Json -Compress"
            result = subprocess.run(["powershell.exe", "-NoProfile", "-Command", command], check=True, capture_output=True, text=True)
            report["loaded_library_paths"] = json.loads(result.stdout)
    report["status"] = "passed"
    with Client(root=package, backend="cpu") as client:
        calibration = client.load_calibration(args.fixtures.resolve() / "identity-model.json")
        result = calibration.apply(args.fixtures.resolve() / "sample.jpg", output_dir=args.output / "identity-output")
        assert result.outputs and all(path.is_file() for path in result.outputs)
        assert result.backends.actual.get("apply") == "cpu", result.backends.actual
        report["cpu_identity_apply"] = {"outputs": [str(p) for p in result.outputs], "backend": result.backends.actual}
    (args.output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
