"""Run native automated checks and record evidence without claiming manual GUI/GPU coverage."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import time
from package_native import native_architecture, executable_architectures, require_architecture, TARGETS


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--engine", type=Path)
    parser.add_argument("--inventory-only", action="store_true")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    args.output.mkdir(parents=True, exist_ok=True)
    process_architecture = {"amd64": "x64", "x86_64": "x64", "aarch64": "arm64", "arm64": "arm64"}.get(platform.machine().lower(), platform.machine())
    # platform.machine() can describe native Windows ARM64 even while this
    # interpreter is an emulated x64 process. Inspect its actual executable.
    process_binary_architectures = executable_architectures(Path(sys.executable).resolve())
    host_slices = {arch for system, arch in process_binary_architectures if system == platform.system()}
    if len(host_slices) == 1:
        process_architecture = next(iter(host_slices))
    architecture = native_architecture()
    if process_architecture != architecture and not args.inventory_only:
        parser.error(f"native acceptance requires a {architecture} Python process; this process is {process_architecture} (possibly emulated)")
    os_id = {"Windows": "win", "Linux": "linux", "Darwin": "osx"}.get(platform.system(), platform.system())
    if not args.inventory_only and f"{os_id}-{architecture}" not in TARGETS:
        parser.error(f"native acceptance is not configured for {os_id}-{architecture}")
    runtime_architectures = {}
    if not args.inventory_only:
        expected = (platform.system(), architecture)
        try:
            if args.engine:
                args.engine = args.engine.resolve(strict=True)
                require_architecture(args.engine, expected)
                runtime_architectures["engine"] = sorted(executable_architectures(args.engine))
            located_dotnet = shutil.which(str(Path(args.dotnet).expanduser()))
            if not located_dotnet:
                raise ValueError("native .NET host not found; supply --dotnet with its executable path")
            dotnet = Path(located_dotnet).resolve(strict=True)
            require_architecture(dotnet, expected)
            runtime_architectures["dotnet"] = sorted(executable_architectures(dotnet))
            # Invoke the inspected executable rather than resolving PATH again
            # or treating a wrapper/emulated host as native acceptance evidence.
            args.dotnet = str(dotnet)
        except (OSError, ValueError) as error:
            parser.error(str(error))
    report = {"target": f"{os_id}-{architecture}", "os": platform.platform(), "python": sys.version,
              "machine": platform.machine(), "process_architecture": process_architecture,
              "native_host": process_architecture == architecture, "checks": [],
              "manual_gui_validation": "pending", "gpu_validation": "pending",
              "release_supported": False}
    if runtime_architectures:
        report["runtime_architectures"] = runtime_architectures
        report["dotnet"] = args.dotnet
    for path in [root / "global.json", root / "engine/Cargo.lock", *(root / "models").rglob("*.onnx")]:
        report.setdefault("sha256", {})[str(path.relative_to(root))] = hashlib.sha256(path.read_bytes()).hexdigest()
    if args.engine:
        report["engine"] = str(args.engine.resolve())
        report["engine_sha256"] = hashlib.sha256(args.engine.read_bytes()).hexdigest()
    if not args.inventory_only:
        env = dict(os.environ, CCP_REQUIRE_UI_BASELINES="1")
        env.pop("CCP_UPDATE_UI_BASELINES", None)
        if args.engine:
            env["CCP_ENGINE_EXE"] = str(args.engine.resolve())
            env["CCP_NATIVE_WORKER_TESTS"] = "1"
        commands = [
            ([args.dotnet, "test", "app/ColorCorrect.sln", "-c", "Release", "--verbosity", "normal"], root),
            (["cargo", "test", "-p", "ccp_core", "-p", "ccp_bridge", "--lib", "--tests", "--locked"], root / "engine"),
            ([sys.executable, "-m", "unittest", "discover", "-s", "api/tests", "-p", "test_*.py"], root),
        ]
        if args.engine:
            commands.append(([sys.executable, "api/tests/integration_sdk.py", "--engine", str(args.engine.resolve())], root))
        for index, (command, cwd) in enumerate(commands):
            started = time.monotonic()
            result = subprocess.run(command, cwd=cwd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
            log = args.output / f"check-{index + 1}.log"
            log.write_text(result.stdout, encoding="utf-8")
            report["checks"].append({"command": command, "exit_code": result.returncode, "seconds": time.monotonic() - started, "log": str(log.resolve())})
            print(f"{'PASS' if result.returncode == 0 else 'FAIL'} {' '.join(command)}", flush=True)
    report["automated_checks_passed"] = bool(report["checks"]) and all(c["exit_code"] == 0 for c in report["checks"])
    (args.output / "native-validation.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    return 0 if args.inventory_only or report["automated_checks_passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
