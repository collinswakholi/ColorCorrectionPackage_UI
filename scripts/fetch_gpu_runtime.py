"""Fetch pinned official native ONNX GPU runtimes; never installs globally."""
from pathlib import Path
import argparse, hashlib, json, urllib.request, zipfile

PACKAGES = {
    "microsoft.ml.onnxruntime": ("1.24.4", "4b978d5065b85e7004b6c6f60ca494bd978fbe6836cbf0a0b52d82b61ab99638"),
    "microsoft.ml.onnxruntime.directml": ("1.24.4", "57e9f11b73437bef7a309496135d4c1f96b1a8e9ddba60013fa27bfc1d788681"),
    "microsoft.ml.onnxruntime.ep.webgpu": ("0.4.0", "cc2e093a2eaa760a630d6c3299f015f1dd86885b9eb5877257135308f7c5e923"),
    "microsoft.ai.directml": ("1.15.4", "4e7cb7ddce8cf837a7a75dc029209b520ca0101470fcdf275c1f49736a3615b9"),
}

# Fixed Microsoft CDN artifacts, inspected for a valid Microsoft signature.
# Downloads occur only during an explicitly requested build, never app startup.
VC_REDIST = {
    "x64": ("14.44.35211.0", "https://download.visualstudio.microsoft.com/download/pr/990c720a-46ce-4dc7-87b9-c2eb615d34b9/CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B/VC_redist.x64.exe", "cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b"),
    "arm64": ("14.44.35211.0", "https://download.visualstudio.microsoft.com/download/pr/d7450eb5-03e1-436d-9e7e-deb5fe4759b3/5139E1440C3A20B92153A4DB561C069A0175AAF76C276C3E5B6F56099EDCF4B0/VC_redist.arm64.exe", "5139e1440c3a20b92153a4db561c069a0175aaf76c276c3e5b6f56099edcf4b0"),
}


def fetch_vc_redist(rid: str, output: Path) -> Path:
    if rid not in ("win-x64", "win-arm64"):
        raise ValueError("VC prerequisite is only applicable to Windows")
    architecture = rid.removeprefix("win-")
    version, url, expected = VC_REDIST[architecture]
    output.mkdir(parents=True, exist_ok=True)
    archive = output / f"vc_redist.{architecture}.exe"
    if not archive.exists():
        temporary = archive.with_suffix(".partial")
        urllib.request.urlretrieve(url, temporary)
        if hashlib.sha256(temporary.read_bytes()).hexdigest() != expected:
            raise ValueError("Microsoft VC prerequisite hash mismatch")
        temporary.replace(archive)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != expected:
        raise ValueError("Cached Microsoft VC prerequisite hash mismatch")
    (output / f"vc_redist.{architecture}.source.json").write_text(json.dumps(
        {"version": version, "url": url, "sha256": expected, "architecture": architecture}, indent=2), encoding="utf-8")
    return archive

def fetch(rid: str, output: Path, cache: Path) -> dict:
    if rid not in ("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-arm64"):
        raise ValueError("Unsupported native target")
    output.mkdir(parents=True, exist_ok=False)
    cache.mkdir(parents=True, exist_ok=True)
    packages = ["microsoft.ml.onnxruntime.directml" if rid.startswith("win") else "microsoft.ml.onnxruntime",
                "microsoft.ml.onnxruntime.ep.webgpu"]
    if rid.startswith("win"): packages.append("microsoft.ai.directml")
    report = {"rid":rid, "packages":[], "files":{}}
    for package in packages:
        version, expected = PACKAGES[package]
        archive = cache / f"{package}.{version}.nupkg"
        url = f"https://api.nuget.org/v3-flatcontainer/{package}/{version}/{package}.{version}.nupkg"
        if not archive.exists():
            temporary = archive.with_suffix(".partial")
            urllib.request.urlretrieve(url, temporary)
            if hashlib.sha256(temporary.read_bytes()).hexdigest() != expected: raise ValueError(f"Hash mismatch: {package}")
            temporary.replace(archive)
        if hashlib.sha256(archive.read_bytes()).hexdigest() != expected: raise ValueError(f"Hash mismatch: {package}")
        report["packages"].append({"id":package,"version":version,"url":url,"sha256":expected})
        with zipfile.ZipFile(archive) as z:
            for name in z.namelist():
                native = name.startswith(f"runtimes/{rid}/native/")
                if package == "microsoft.ai.directml":
                    native = name.lower() == f"bin/{'arm64' if rid=='win-arm64' else 'x64'}-win/directml.dll"
                if native and name.endswith((".dll", ".so", ".dylib")):
                    (output / Path(name).name).write_bytes(z.read(name))
                if Path(name).name.lower() in ("license", "license.txt", "thirdpartynotices.txt", "third_party_notices.txt"):
                    notices = output / "licenses" / package
                    notices.mkdir(parents=True, exist_ok=True)
                    (notices / Path(name).name).write_bytes(z.read(name))
    for p in output.rglob("*"):
        if p.is_file(): report["files"][p.relative_to(output).as_posix()] = hashlib.sha256(p.read_bytes()).hexdigest()
    required = ["onnxruntime.dll", "DirectML.dll", "onnxruntime_providers_webgpu.dll"] if rid.startswith("win") else ["libonnxruntime.dylib", "libonnxruntime_providers_webgpu.dylib"] if rid.startswith("osx") else ["libonnxruntime.so", "libonnxruntime_providers_webgpu.so"]
    for name in required:
        if not (output / name).is_file(): raise ValueError(f"Pinned packages missing {rid}/{name}")
    (output / "gpu-runtime-manifest.json").write_text(json.dumps(report, indent=2))
    return report

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--cache", type=Path, default=Path(__file__).resolve().parents[1]/"_implementation/runtime-cache")
    parser.add_argument("--vc-redist-output", type=Path, help="also fetch a pinned offline Windows VC prerequisite; it is not installed")
    args=parser.parse_args()
    print(json.dumps(fetch(args.rid, args.output, args.cache), indent=2))
    if args.vc_redist_output:
        print(fetch_vc_redist(args.rid, args.vc_redist_output))
