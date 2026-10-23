"""Stage the official, signature-verified Microsoft VC prerequisite offline."""
from pathlib import Path
import json
import os
import re
import shutil
import subprocess
from package_support import digest


def stage_redist(source: Path, payload: Path, rid: str) -> dict:
    architecture = "arm64" if "arm64" in source.name.lower() else "x64" if "x64" in source.name.lower() else None
    if architecture is None or (rid == "win-x64" and architecture != "x64"):
        raise ValueError("Supply an official vc_redist.x64.exe or vc_redist.arm64.exe matching this package")
    # Microsoft's x64 v14 redist also supplies native ARM64 components on ARM64.
    verifier = Path(__file__).parents[1] / "installer/windows/verify-vc-redist.ps1"
    environment = dict(os.environ)
    # A build shell may export PowerShell 7 module paths into Windows PowerShell 5.
    # Signature verification needs only the trusted operating-system modules.
    environment["PSModulePath"] = str(Path(os.environ["SystemRoot"]) / "System32/WindowsPowerShell/v1.0/Modules")
    result = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(verifier),
                             "-Path", str(source)], check=False, capture_output=True, text=True, env=environment)
    if result.returncode:
        raise ValueError("VC signature verification failed: " + result.stderr.strip())
    metadata = json.loads(result.stdout)
    if not re.fullmatch(r"14\.\d+\.\d+\.\d+", metadata["version"]):
        raise ValueError("Unexpected VC redistributable version")
    destination = payload / "prerequisites"
    destination.mkdir()
    shutil.copy2(source, destination / "vc_redist.exe")
    metadata.update(schema=1, architecture=architecture, target=rid, sha256=digest(source), filename="vc_redist.exe")
    (destination / "vc-redist.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    return metadata
