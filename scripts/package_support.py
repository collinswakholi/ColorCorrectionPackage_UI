"""Release payload contracts. Build-time only; installed apps never need Python."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import xml.etree.ElementTree as ET

MODELS = (
    "models/opt2/best_assemble_fp32_1280.onnx",
    "models/opt2/best_assemble_fp32_1536.onnx",
    "models/plane/plane_det_model_YOLO_512_n.onnx",
)
REFERENCES = ("refs/neutrals.json", "refs/digital_sg_lab_d50.json")
REQUIRED_NOTICES = ("LICENSE", "THIRD_PARTY_NOTICES.md", "models/NOTICE.md", "models/LICENSE-AGPL-3.0.txt")
DOCS = ("docs/user-guide.md", "docs/model-provenance.md", "docs/color-science-validation.md", "docs/api/README.md", "docs/api/protocol.md",
        "api/README.md", "docs/images/workflow.svg", "docs/images/session-workbench.png", "docs/images/process-controls.png", "docs/images/in-app-guide.png")
RUNTIMES = {
    "Windows": ("onnxruntime.dll", "onnxruntime_providers_webgpu.dll", "DirectML.dll"),
    "Linux": ("libonnxruntime.so", "libonnxruntime_providers_webgpu.so"),
    "Darwin": ("libonnxruntime.dylib", "libonnxruntime_providers_webgpu.dylib"),
}
VC_RUNTIME_NAMES = {"vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll",
                    "msvcp140_2.dll", "msvcp140_atomic_wait.dll", "msvcp140_codecvt_ids.dll", "concrt140.dll"}


def native_version(root: Path) -> str:
    """The installer and archives use the concrete application version."""
    version = ET.parse(root / "app/ColorCorrect.App/ColorCorrect.App.csproj").findtext(".//Version", "").strip()
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("The application project must declare a concrete major.minor.patch Version")
    return version


def source_identity(root: Path) -> dict:
    """Describe this tree without attributing a plain archive to an ancestor Git repo."""
    root = root.resolve()
    identity = {"git_commit": None, "working_tree_dirty": None}
    manifest = root / "source-manifest.json"
    if manifest.is_file():
        identity["source_manifest_sha256"] = digest(manifest)
    if not (root / ".git").exists():
        return identity
    try:
        top = subprocess.run(["git", "-C", str(root), "rev-parse", "--show-toplevel"],
                             check=True, capture_output=True, text=True)
        if Path(top.stdout.strip()).resolve() != root:
            return identity
        commit = subprocess.run(["git", "-C", str(root), "rev-parse", "HEAD"],
                                check=True, capture_output=True, text=True).stdout.strip()
        if not re.fullmatch(r"[0-9a-fA-F]{40,64}", commit):
            return identity
        status = subprocess.run(["git", "-C", str(root), "status", "--porcelain", "--untracked-files=normal"],
                                check=True, capture_output=True, text=True)
        identity.update(git_commit=commit.lower(), working_tree_dirty=bool(status.stdout))
    except (OSError, subprocess.CalledProcessError):
        pass  # Source archives need neither Git nor invented revision metadata.
    return identity


def digest(path: Path) -> str:
    with path.open("rb") as source:
        result = hashlib.sha256()
        for block in iter(lambda: source.read(1024 * 1024), b""):
            result.update(block)
        return result.hexdigest()


def check_model_manifest(root: Path) -> dict:
    manifest = json.loads((root / "models/manifest.json").read_text(encoding="utf-8"))
    entries = {entry["path"]: entry for entry in manifest["files"]}
    # Accept model-relative paths in source manifests, but never paths outside models/.
    normalized = {key if key.startswith("models/") else "models/" + key: value for key, value in entries.items()}
    if set(normalized) != set(MODELS):
        raise ValueError("Model manifest must contain exactly the three bundled detector models")
    for relative, entry in normalized.items():
        path = root / relative
        if path.is_symlink() or digest(path) != entry["sha256"]:
            raise ValueError("Model hash mismatch or link: " + relative)
    return manifest


def check_runtime_manifest(runtime: Path, rid: str, system: str) -> dict:
    manifest = json.loads((runtime / "gpu-runtime-manifest.json").read_text(encoding="utf-8"))
    if manifest.get("rid") != rid:
        raise ValueError("GPU runtime manifest architecture does not match package")
    for filename in RUNTIMES[system]:
        if not (runtime / filename).is_file():
            raise ValueError("Missing required GPU runtime: " + filename)
    for relative, expected in manifest["files"].items():
        path = (runtime / relative).resolve(strict=True)
        if not path.is_relative_to(runtime.resolve()) or digest(path) != expected:
            raise ValueError("GPU runtime checksum mismatch: " + relative)
    for package in manifest["packages"]:
        notices = runtime / "licenses" / package["id"]
        if not notices.is_dir() or not any(p.is_file() for p in notices.iterdir()):
            raise ValueError("Missing runtime notices: " + package["id"])
    return manifest


def pe_details(path: Path) -> tuple[bool, list[str]]:
    """Return managed-PE flag and imported DLL names (no target code execution)."""
    data = path.read_bytes()
    if data[:2] != b"MZ" or len(data) < 64:
        return False, []
    offset = struct.unpack_from("<I", data, 0x3c)[0]
    if offset + 24 > len(data):
        return False, []
    sections, optional_size = struct.unpack_from("<H12xH", data, offset + 6)
    optional = offset + 24
    magic = struct.unpack_from("<H", data, optional)[0]
    table = optional + (112 if magic == 0x20b else 96)
    if table + 15 * 8 > optional + optional_size:
        return False, []
    managed = struct.unpack_from("<II", data, table + 14 * 8) != (0, 0)
    section_table = optional + optional_size

    def location(rva: int) -> int:
        for i in range(sections):
            at = section_table + i * 40
            virtual_size, virtual, raw_size, raw = struct.unpack_from("<IIII", data, at + 8)
            if virtual <= rva < virtual + max(virtual_size, raw_size):
                return raw + rva - virtual
        raise ValueError("Invalid PE import address: " + str(path))

    imports = []
    import_rva, _ = struct.unpack_from("<II", data, table + 8)
    if import_rva:
        at = location(import_rva)
        while at + 20 <= len(data) and any(data[at:at + 20]):
            name_at = location(struct.unpack_from("<I", data, at + 12)[0])
            imports.append(data[name_at:data.index(0, name_at)].decode("ascii").lower())
            at += 20
    return managed, imports


def dependency_audit(payload: Path, system: str, require_architecture, expected: tuple[str, str]) -> dict:
    """Inspect all shipped native files and resolve OS vs app dependencies."""
    native = []
    for path in payload.rglob("*"):
        if not path.is_file():
            continue
        with path.open("rb") as stream:
            magic = stream.read(4)
        if magic[:2] == b"MZ":
            managed, imports = pe_details(path)
            if managed:
                continue
            # The official VC bootstrapper may be x86; installed runtime architecture
            # is recorded/checked separately and it is not an app runtime DLL.
            if path.parent.name == "prerequisites":
                continue
            require_architecture(path, expected)
            native.append((path, imports))
        elif magic == b"\x7fELF" or magic in (b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf", b"\xca\xfe\xba\xbe"):
            require_architecture(path, expected)
            native.append((path, []))
    report = {"native_files": [], "system_dependencies": [], "missing": [], "optional_diagnostics": []}
    shipped = {p.name.lower(): p for p in payload.rglob("*") if p.is_file()}
    for path, imports in native:
        record = {"path": path.relative_to(payload).as_posix(), "dependencies": []}
        if system == "Windows":
            for name in imports:
                if name in shipped:
                    resolved = shipped[name].relative_to(payload).as_posix()
                elif name in VC_RUNTIME_NAMES and (payload / "prerequisites/vc-redist.json").is_file():
                    resolved = "optional VC runtime prerequisite (required before launch)"
                elif name.startswith(("api-ms-", "ext-ms-")):
                    resolved = "Windows API set"
                else:
                    candidate = Path(os.environ.get("SystemRoot", "C:/Windows")) / "System32" / name
                    resolved = str(candidate) if candidate.is_file() else None
                record["dependencies"].append({"name": name, "resolved": resolved})
                if resolved is None:
                    report["missing"].append(f"{record['path']}: {name}")
        elif system == "Linux":
            env = dict(os.environ, LD_LIBRARY_PATH=str(payload) + os.pathsep + str(payload / "app"))
            checked = subprocess.run(["ldd", str(path)], capture_output=True, text=True, env=env)
            for line in checked.stdout.splitlines():
                line = line.strip()
                if line:
                    record["dependencies"].append(line)
                if "not found" in line:
                    if path.name == "libcoreclrtraceptprovider.so" and line.startswith("liblttng-ust.so.0 "):
                        # CoreCLR loads this legacy LTTng adapter only for optional
                        # native tracing. Ubuntu 24.04 ships the newer ABI. Keep
                        # the adapter and expose this limitation; EventPipe/app
                        # diagnostics and the application do not depend on it.
                        report["optional_diagnostics"].append(f"{record['path']}: legacy LTTng tracing unavailable ({line})")
                    else:
                        report["missing"].append(f"{record['path']}: {line}")
            if checked.returncode and "not a dynamic executable" not in checked.stderr and "statically linked" not in checked.stdout:
                report["missing"].append(f"Unable to inspect {record['path']}: {checked.stderr.strip()}")
        report["native_files"].append(record)
    if report["missing"]:
        raise ValueError("Unresolved package dependencies: " + "; ".join(report["missing"]))
    report["status"] = "build-host closure checked; clean installation remains required"
    return report
