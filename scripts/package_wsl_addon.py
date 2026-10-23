"""Create the optional, relocatable Mesa 25.2.8 WSL x64 runtime add-on."""
from pathlib import Path
import argparse
import json
import shutil
import subprocess
from package_support import digest

MESA_VERSION = "25.2.8"
MESA_SHA256 = "097842f3e49d996868b38688db87b006f7d4541e93ce86d2f341d8b3e7be7c93"
HEADERS_COMMIT = "c94b9b23aaadc2034dd1cad656a5a69f1526f98a"


def validate_addon(root: Path, rid: str) -> dict:
    from package_native import require_architecture
    root = root.resolve(strict=True)
    manifest = json.loads((root / "manifest.json").read_text(encoding="utf-8"))
    if rid != "linux-x64" or manifest.get("rid") != rid or manifest.get("mesa_version") != MESA_VERSION:
        raise ValueError("Only the reviewed Mesa 25.2.8 WSL x64 add-on is currently eligible")
    if manifest.get("experimental") is not True:
        raise ValueError("WSL add-on must retain its experimental status")
    actual = {path.relative_to(root).as_posix() for path in root.rglob("*") if path.is_file()}
    unexpected = actual - set(manifest["sha256"]) - {"manifest.json", "checksums.sha256"}
    if unexpected:
        raise ValueError("Untracked WSL add-on files: " + ", ".join(sorted(unexpected)))
    for relative, expected in manifest["sha256"].items():
        source = root / relative
        path = source.resolve(strict=True)
        if not path.is_relative_to(root) or source.is_symlink() or digest(path) != expected:
            raise ValueError("WSL add-on checksum mismatch: " + relative)
    if not {"lib/libvulkan_dzn.so", "icd/dzn_icd.json"}.issubset(manifest["sha256"]):
        raise ValueError("WSL add-on is incomplete")
    require_architecture(root / "lib/libvulkan_dzn.so", ("Linux", "x64"))
    icd = json.loads((root / "icd/dzn_icd.json").read_text(encoding="utf-8"))
    if icd["ICD"]["library_path"] != "../lib/libvulkan_dzn.so":
        raise ValueError("WSL ICD must be relocatable and reference its private driver")
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prefix", type=Path, required=True)
    parser.add_argument("--mesa-source", type=Path, required=True)
    parser.add_argument("--headers-source", type=Path, required=True)
    parser.add_argument("--build-provenance", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    source = args.mesa_source.resolve(strict=True)
    if (source / "VERSION").read_text().strip() != MESA_VERSION:
        raise ValueError("A different Mesa version requires new correctness/driver acceptance")
    provenance = json.loads(args.build_provenance.read_text())
    if provenance.get("mesa_sha256") != MESA_SHA256 or provenance.get("headers_commit") != HEADERS_COMMIT:
        raise ValueError("Unreviewed driver source provenance")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    (output / "lib").mkdir()
    library = args.prefix / "lib/x86_64-linux-gnu/libvulkan_dzn.so"
    shutil.copy2(library, output / "lib/libvulkan_dzn.so")
    (output / "icd").mkdir()
    (output / "icd/dzn_icd.json").write_text(json.dumps({"file_format_version": "1.0.1", "ICD": {
        "api_version": "1.1.318", "library_arch": "64", "library_path": "../lib/libvulkan_dzn.so"}}, indent=2), encoding="utf-8")
    notices = output / "licenses"
    notices.mkdir()
    shutil.copytree(source / "licenses", notices / "mesa")
    shutil.copy2(source / "docs/license.rst", notices / "mesa-license.rst")
    shutil.copy2(args.headers_source / "LICENSE", notices / "DirectX-Headers-LICENSE")
    shutil.copy2(args.build_provenance, output / "build-provenance.json")
    inspection = subprocess.run(["readelf", "-d", str(output / "lib/libvulkan_dzn.so")], check=True, capture_output=True, text=True)
    (output / "elf-dependencies.txt").write_text(inspection.stdout, encoding="utf-8")
    files = {p.relative_to(output).as_posix(): digest(p) for p in sorted(output.rglob("*")) if p.is_file()}
    manifest = {"schema": 1, "rid": "linux-x64", "mesa_version": MESA_VERSION,
        "experimental": True, "native_validation": "pending for this rebuilt artifact",
        "host_requirements": ["WSL 2", "/dev/dxg", "/usr/lib/wsl/lib/libd3d12.so", "/usr/lib/wsl/lib/libdxcore.so"],
        "system_packages": ["libc6", "libgcc-s1", "libstdc++6", "zlib1g", "libzstd1", "libdrm2", "libexpat1", "libvulkan1"],
        "sha256": files}
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    files["manifest.json"] = digest(output / "manifest.json")
    (output / "checksums.sha256").write_text("".join(f"{value}  {name}\n" for name, value in sorted(files.items())), encoding="utf-8")
    validate_addon(output, "linux-x64")
    print(output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
