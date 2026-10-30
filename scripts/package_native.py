"""Stage a package on its native OS/architecture using reviewed local runtimes.

This complements the existing release scripts. It never downloads a substitute
runtime or marks an untested target supported. No signing/publishing is performed.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import plistlib
import shutil
import struct
import subprocess
import sys
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent))
from package_support import (MODELS, REFERENCES, REQUIRED_NOTICES, DOCS, RUNTIMES,
    check_model_manifest, check_runtime_manifest, dependency_audit, digest, native_version, source_identity)

TARGETS = {
    "win-x64": ("Windows", "x64", "x86_64-pc-windows-msvc"),
    "win-arm64": ("Windows", "arm64", "aarch64-pc-windows-msvc"),
    "linux-x64": ("Linux", "x64", "x86_64-unknown-linux-gnu"),
    "linux-arm64": ("Linux", "arm64", "aarch64-unknown-linux-gnu"),
    "osx-arm64": ("Darwin", "arm64", "aarch64-apple-darwin"),
}
PACKAGE_TARGETS = {key: value for key, value in TARGETS.items() if not key.startswith("osx")}


def native_architecture() -> str:
    machine = platform.machine().lower()
    if platform.system() == "Windows":
        # An x64 Python process on ARM64 does not make an Intel host.
        machine = os.environ.get("PROCESSOR_ARCHITEW6432", machine).lower()
    elif platform.system() == "Darwin" and machine == "x86_64":
        translated = subprocess.run(["sysctl", "-in", "sysctl.proc_translated"],
                                    capture_output=True, text=True, check=False)
        if translated.returncode == 0 and translated.stdout.strip() == "1":
            machine = "arm64"
    return {"amd64": "x64", "x86_64": "x64", "aarch64": "arm64", "arm64": "arm64"}.get(machine, machine)


def executable_architectures(path: Path) -> set[tuple[str, str]]:
    """Read 64-bit executable headers, including universal macOS binaries.

    This checks architecture, not dynamic dependency closure or executability.
    Those still require launching the extracted package on the target machine.
    """
    with path.open("rb") as stream:
        size = path.stat().st_size
        header = stream.read(64)
        if header[:2] == b"MZ":
            if len(header) < 64:
                raise ValueError(f"truncated PE header: {path}")
            stream.seek(struct.unpack_from("<I", header, 0x3C)[0])
            pe = stream.read(6)
            if len(pe) != 6 or pe[:4] != b"PE\0\0":
                raise ValueError(f"invalid PE executable: {path}")
            return {("Windows", {0x8664: "x64", 0xAA64: "arm64"}.get(struct.unpack_from("<H", pe, 4)[0], "unknown"))}
        if header[:4] == b"\x7fELF":
            if len(header) < 64 or header[4] != 2 or header[5] not in (1, 2) or header[6] != 1:
                raise ValueError(f"invalid 64-bit ELF header: {path}")
            endian = "<" if header[5] == 1 else ">"
            return {("Linux", {62: "x64", 183: "arm64"}.get(struct.unpack_from(endian + "H", header, 18)[0], "unknown"))}
        if header[:4] in (b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf"):
            if len(header) < 32:
                raise ValueError(f"truncated Mach-O header: {path}")
            endian = "<" if header[:4] == b"\xcf\xfa\xed\xfe" else ">"
            return {("Darwin", {0x01000007: "x64", 0x0100000C: "arm64"}.get(struct.unpack_from(endian + "I", header, 4)[0], "unknown"))}
        fat_formats = {b"\xca\xfe\xba\xbe": (">", False), b"\xbe\xba\xfe\xca": ("<", False),
                       b"\xca\xfe\xba\xbf": (">", True), b"\xbf\xba\xfe\xca": ("<", True)}
        if header[:4] in fat_formats:
            endian, fat64 = fat_formats[header[:4]]
            if len(header) < 8:
                raise ValueError(f"truncated universal Mach-O header: {path}")
            count = struct.unpack_from(endian + "I", header, 4)[0]
            entry_size = 32 if fat64 else 20
            if not 1 <= count <= 64 or 8 + count * entry_size > size:
                raise ValueError(f"invalid universal Mach-O table: {path}")
            architectures = set()
            stream.seek(8)
            table = stream.read(count * entry_size)
            for index in range(count):
                entry = table[index * entry_size:(index + 1) * entry_size]
                cpu = struct.unpack_from(endian + "I", entry)[0]
                offset, length = struct.unpack_from(endian + ("QQ" if fat64 else "II"), entry, 8)
                if length < 32 or offset < 8 + len(table) or offset + length > size:
                    raise ValueError(f"invalid universal Mach-O slice: {path}")
                stream.seek(offset)
                thin = stream.read(32)
                magic = thin[:4]
                if magic not in (b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf"):
                    raise ValueError(f"invalid universal Mach-O slice header: {path}")
                slice_endian = "<" if magic == b"\xcf\xfa\xed\xfe" else ">"
                if struct.unpack_from(slice_endian + "I", thin, 4)[0] != cpu:
                    raise ValueError(f"universal Mach-O CPU mismatch: {path}")
                architectures.add(("Darwin", {0x01000007: "x64", 0x0100000C: "arm64"}.get(cpu, "unknown")))
            return architectures
    raise ValueError(f"unrecognized native executable (64-bit PE/ELF/Mach-O required): {path}")


def require_architecture(path: Path, expected: tuple[str, str]) -> None:
    if expected not in executable_architectures(path):
        raise ValueError(f"native architecture does not match {expected}: {path}")


def copy_tree(source: Path, destination: Path) -> None:
    shutil.copytree(source, destination, ignore=shutil.ignore_patterns(
        "__pycache__", "*.pyc", ".venv*", "bin", "obj", "build", "*.egg-info",
        "*.dist-info", ".ipynb_checkpoints"))


def windows_launcher(rid: str = "win-x64") -> str:
    if rid not in ("win-x64", "win-arm64"):
        raise ValueError("Windows launcher requires win-x64 or win-arm64")
    return ('@echo off\nsetlocal DisableDelayedExpansion\n'
            'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0check-system.ps1"\n'
            'if errorlevel 1 exit /b 1\n'
            'set "CCP_NEW_UI_ROOT=%~dp0"\n'
            'set "CCP_ENGINE_EXE=%~dp0ccp-engine.exe"\n'
            'set "ORT_DYLIB_PATH=%~dp0onnxruntime.dll"\n'
            'set "PATH=%~dp0;%PATH%"\n'
            '"%~dp0app\\ColorCorrect.App.exe" %*\n')


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", choices=PACKAGE_TARGETS)
    parser.add_argument("--describe", action="store_true")
    parser.add_argument("--worker", type=Path)
    parser.add_argument("--ort-runtime", type=Path, help="directory of architecture-correct ONNX Runtime native libraries")
    parser.add_argument("--features", default="ort-plane,wgpu-lut,rust-inference", help="features used for the supplied native worker")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--app-publish", type=Path, help="use an already-published self-contained .NET app directory; apphost architecture is verified")
    parser.add_argument("--vc-redist", type=Path, help="official Microsoft VC redistributable; required for Windows packages")
    parser.add_argument("--wheel", type=Path, help="prebuilt Python wheel (otherwise build locally without dependency downloads)")
    parser.add_argument("--wsl-addon", type=Path, help="reviewed experimental x64 WSL add-on directory; never enabled automatically")
    parser.add_argument("--dependency-notices", type=Path, help="precollected complete license inventory for this target")
    parser.add_argument("--fetch-notices", action="store_true", help="explicitly fetch missing upstream license texts at pinned source commits")
    parser.add_argument("--deb", action="store_true", help="also build an Ubuntu 24.04 Debian package")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--no-archive", action="store_true", help="Stage and hash the payload without compressing an archive")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    if args.describe:
        print(json.dumps({rid: {"os": value[0], "architecture": value[1], "rust_target": value[2],
                               "native_validation": "required, not implied by packaging"}
                          for rid, value in PACKAGE_TARGETS.items()}, indent=2))
        return 0
    if not args.rid or not args.worker or not args.ort_runtime:
        parser.error("--rid, --worker, and --ort-runtime are required")
    features = [feature.strip() for feature in args.features.split(",") if feature.strip()]
    if "video" in features:
        parser.error("Video processing has been removed; package still-image workers only")
    if set(features) & {"tch", "cuda", "libtorch", "torch"}:
        parser.error("Removed inference features cannot be included in GPU-only packages")
    expected = TARGETS[args.rid][:2]
    if (platform.system(), native_architecture()) != expected:
        parser.error(f"run natively on {expected}; current process is {(platform.system(), native_architecture())}")
    native_name = {"Windows": "onnxruntime.dll", "Linux": "libonnxruntime.so", "Darwin": "libonnxruntime.dylib"}[expected[0]]
    pattern = "*.dll" if expected[0] == "Windows" else ("*.so*" if expected[0] == "Linux" else "*.dylib")
    try:
        worker = args.worker.resolve(strict=True)
        require_architecture(worker, expected)
        runtime = args.ort_runtime.resolve(strict=True)
        if not runtime.is_dir():
            raise ValueError("--ort-runtime must be a directory")
        runtime_libraries = sorted(library for library in runtime.glob(pattern) if library.is_file())
        if any("torch" in library.name.lower() or library.name.lower().startswith("c10") for library in runtime_libraries):
            raise ValueError("Removed ML runtime libraries must not be included in GPU packages")
        # A versioned .so alone is common in local runtime trees. Materialize its
        # canonical loader name below, while retaining the versioned dependency.
        libraries = [library for library in runtime_libraries if library.name == native_name or
                     (expected[0] == "Linux" and library.name.startswith(native_name + ".")) or
                     (expected[0] == "Darwin" and library.match("libonnxruntime.[0-9]*.dylib"))]
        if not libraries:
            raise ValueError(f"missing {native_name} in --ort-runtime")
        ort_primary = next((library for library in libraries if library.name == native_name), libraries[0])
        for library in runtime_libraries:
            require_architecture(library, expected)
        runtime_manifest = check_runtime_manifest(runtime, args.rid, expected[0])
        untracked_libraries = [library.name for library in runtime_libraries if library.name not in runtime_manifest["files"]]
        if untracked_libraries:
            raise ValueError("Runtime libraries lack reviewed hashes: " + ", ".join(untracked_libraries))
        root = args.root.resolve(strict=True)
        if not (root / "models/opt2").is_dir() or not (root / "refs/neutrals.json").is_file():
            raise ValueError("--root must contain models/opt2 and refs/neutrals.json for runtime discovery")
        model_manifest = check_model_manifest(root)
        for notice in REQUIRED_NOTICES:
            if not (root / notice).is_file():
                raise ValueError("Missing redistribution notice: " + notice)
        if expected[0] == "Windows" and not args.vc_redist:
            raise ValueError("--vc-redist is required so offline installation can repair missing VC dependencies")
        if args.wsl_addon and args.rid != "linux-x64":
            raise ValueError("The experimental WSL add-on is validated only for linux-x64")
        if args.deb and expected[0] != "Linux":
            raise ValueError("--deb requires a native Linux package")
        prebuilt = args.app_publish.resolve(strict=True) if args.app_publish else None
        if prebuilt:
            require_architecture(prebuilt / ("ColorCorrect.App.exe" if expected[0] == "Windows" else "ColorCorrect.App"), expected)
    except (OSError, ValueError) as error:
        parser.error(str(error))
    try:
        version = native_version(root)
    except (OSError, ValueError) as error:
        parser.error(str(error))
    output = (args.output or root / "dist" / f"native-{args.rid}-{uuid.uuid4().hex[:8]}").resolve()
    if any(output == root / folder or root / folder in output.parents
           for folder in ("models", "refs", "api", "docs", "notebooks")):
        parser.error("--output cannot be inside a copied source directory")
    output.mkdir(parents=True, exist_ok=False)
    stage = output / "payload"
    payload = stage
    publish = stage / "app"
    payload.mkdir(parents=True)
    if prebuilt:
        shutil.copytree(prebuilt, publish, dirs_exist_ok=True, ignore=shutil.ignore_patterns("*.pdb"))
    else:
        subprocess.run([args.dotnet, "publish", str(root / "app/ColorCorrect.App/ColorCorrect.App.csproj"),
                        "-c", "Release", "-r", args.rid, "--self-contained", "true", "--nologo",
                        "-p:UseAppHost=true", "-p:PublishSingleFile=false", "-p:RestoreLockedMode=true",
                        "-p:DebugType=none", "-p:DebugSymbols=false", "-o", str(publish)], check=True)
    # NuGet native assets can carry PDBs despite DebugSymbols=false. Setup
    # excludes these development files, so omit them before hashing either
    # distribution rather than producing a manifest the installer cannot meet.
    for symbols in publish.rglob("*"):
        if symbols.is_file() and symbols.suffix.lower() == ".pdb":
            symbols.resolve(strict=True).relative_to(publish.resolve(strict=True))
            symbols.unlink()
    application_name = "ColorCorrect.App.exe" if expected[0] == "Windows" else "ColorCorrect.App"
    require_architecture(publish / application_name, expected)
    if expected[0] != "Windows":
        apphost = publish / application_name
        apphost.chmod(apphost.stat().st_mode | 0o111)
    worker_destination = payload / ("ccp-engine.exe" if expected[0] == "Windows" else "ccp-engine")
    shutil.copy2(worker, worker_destination)
    if expected[0] != "Windows":
        worker_destination.chmod(worker_destination.stat().st_mode | 0o111)
    for library in runtime_libraries:
        shutil.copy2(library, payload / library.name, follow_symlinks=True)
    runtime_notices = runtime / "licenses"
    if not runtime_notices.is_dir():
        runtime_notices = runtime / "runtime-licenses"
    if runtime_notices.is_dir():
        copy_tree(runtime_notices, payload / "runtime-licenses")
    if (runtime / "gpu-runtime-manifest.json").is_file():
        shutil.copy2(runtime / "gpu-runtime-manifest.json", payload / "gpu-runtime-manifest.json")
    if not (payload / native_name).is_file():
        shutil.copy2(ort_primary, payload / native_name)
    # Explicit payload list: no research logs, source tests, build caches, obsolete
    # graphs, private photographs, notebooks or development executables.
    for relative in (*MODELS, *REFERENCES, *REQUIRED_NOTICES, *DOCS, "models/manifest.json"):
        source = root / relative
        if source.is_file():
            destination = payload / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, destination)
    wheel_dir = payload / "python"
    wheel_dir.mkdir()
    if args.wheel:
        shutil.copy2(args.wheel.resolve(strict=True), wheel_dir / args.wheel.name)
    else:
        subprocess.run([sys.executable, "-m", "pip", "wheel", "--no-deps", "--no-build-isolation",
                        "--wheel-dir", str(wheel_dir), str(root / "api")], check=True)
    wheels = list(wheel_dir.glob("*.whl"))
    if len(wheels) != 1:
        raise ValueError("Package must contain exactly one Python SDK wheel")
    for source in sorted((root / "api/examples").glob("*.py")):
        examples = wheel_dir / "examples"
        examples.mkdir(exist_ok=True)
        shutil.copy2(source, examples / source.name)
    shutil.copy2(root / "installer/PACKAGE-README.md", payload / "README.md")
    from collect_dependency_notices import collect, validate_notices
    notices = args.dependency_notices
    if notices is None:
        notices = output / "dependency-notices"
        collect(root, args.rid, publish, notices, root / "_implementation/notice-cache", args.fetch_notices)
    notice_report = validate_notices(notices, args.rid, root, publish)
    copy_tree(notices, payload / "licenses/dependencies")
    (payload / "package-architecture").write_text(expected[1] + "\n", encoding="ascii")
    if args.wsl_addon:
        from package_wsl_addon import validate_addon
        validate_addon(args.wsl_addon, args.rid)
        copy_tree(args.wsl_addon, payload / "addons/wsl-gpu")
    if expected[0] == "Windows":
        from package_windows_runtime import stage_redist
        vc_runtime = stage_redist(args.vc_redist.resolve(strict=True), payload, args.rid)
        for source in (root / "installer/windows").glob("*.ps1"):
            shutil.copy2(source, payload / source.name)
        launcher = stage / "ColorCorrect-Studio.cmd"
        launcher.write_text(windows_launcher(args.rid), encoding="utf-8")
        # ISPP input belongs to the build directory, not the installed/portable
        # payload. Every manifest entry must exist after either distribution path.
        (output / "installer-metadata.iss").write_text(
            f'#define MyAppVersion "{version}"\n#define PackageRid "{args.rid}"\n'
            f'#define VcVersion "{vc_runtime["version"]}"\n#define VcSha256 "{vc_runtime["sha256"]}"\n', encoding="utf-8")
    else:
        launcher = payload / "ColorCorrect-Studio"
        for name in ("ColorCorrect-Studio", "ccp-engine-launch", "check-system", "wsl-gpu-setup", "install-dependencies", "setup"):
            destination = payload / name
            shutil.copy2(root / "installer/linux" / name, destination)
            destination.chmod(0o755)
    instruction = root / "installer" / ("windows/INSTALL.txt" if expected[0] == "Windows" else "linux/INSTALL.txt")
    shutil.copy2(instruction, payload / "INSTALL.txt")
    from setup_components import component_manifest
    components = component_manifest(payload, args.rid, runtime_manifest, version)
    optional = next(component for component in components["components"] if component["id"] == "webgpu")
    for file in optional["files"]:
        backup = payload / optional["bundled_source"]
        backup.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(payload / file["path"], backup)
    (payload / "setup-components.json").write_text(json.dumps(components, indent=2), encoding="utf-8")
    for component in components["components"]:
        if component["id"] in ("cpu", "webgpu"):
            name = "required" if component["id"] == "cpu" else "webgpu"
            (payload / f"setup-{name}.sha256").write_text("".join(
                f"{file['sha256']}  {file['path']}\n" for file in component["files"]), encoding="ascii")
    audit = dependency_audit(payload, expected[0], require_architecture, expected)
    (payload / "dependency-report.json").write_text(json.dumps(audit, indent=2), encoding="utf-8")
    (payload / "runtime-support.json").write_text(json.dumps({
        "schema": 1, "rid": args.rid, "version": version,
        "inference": "Auto GPU inference with explicit CPU recovery; CPU inference selectable", "correction_cpu_fallback": True,
        "wsl_addon": {"installed": bool(args.wsl_addon), "default_enabled": False, "status": "experimental; x64 only"},
        "minimum_os": "Windows 11" if expected[0] == "Windows" else "Ubuntu 24.04",
        "native_acceptance": "pending", "redistribution_review": model_manifest.get("redistribution_review", "pending")
    }, indent=2), encoding="utf-8")
    hashes = {}
    for file in stage.rglob("*"):
        if file.is_file():
            with file.open("rb") as stream:
                hasher = hashlib.sha256()
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    hasher.update(block)
                hashes[file.relative_to(stage).as_posix()] = hasher.hexdigest()
    manifest = {"schema": 2, "rid": args.rid, "version": version, "source": source_identity(root),
                "rust_target": TARGETS[args.rid][2], "native_validation": "pending",
                "third_party_redistribution_review": "dependency notice collection complete; model training provenance and distribution obligations remain under review",
                "worker_features_declared": features, "inference_policy": "Auto GPU then explicit CPU inference; forced GPU engines fail clearly",
                "media_scope": "still images", "sha256": hashes,
                "limitations": ["Architecture validation does not prove dynamic dependency closure.",
                                "Native launch, workflow validation, signing and notarization remain outstanding."]}
    (output / "package-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    # This manifest deliberately stays outside its own hash set.
    shutil.copy2(output / "package-manifest.json", payload / "package-manifest.json")
    if args.deb:
        from package_deb import build_deb
        build_deb(payload, output, version, args.rid)
    if args.no_archive:
        print(stage)
        return 0
    archive = shutil.make_archive(str(output / f"ColorCorrect-Studio-{version}-{args.rid}"),
                                 "zip" if expected[0] == "Windows" else "gztar", root_dir=stage)
    (output / "SHA256SUMS").write_text("".join(f"{digest(path)}  {path.name}\n" for path in sorted(output.iterdir())
        if path.is_file() and path.suffix in (".zip", ".gz", ".deb")), encoding="ascii")
    print(archive)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
