"""Build the installed, architecture-specific component contract from verified payloads."""
from pathlib import Path
import hashlib


def component_manifest(payload: Path, rid: str, runtime: dict, version: str) -> dict:
    windows = rid.startswith("win-")
    def files(names):
        return [{"path": name, "sha256": hashlib.sha256((payload / name).read_bytes()).hexdigest()}
                for name in names if (payload / name).is_file()]
    core = ["onnxruntime.dll", "DirectML.dll"] if windows else ["libonnxruntime.so"]
    plugin = "onnxruntime_providers_webgpu.dll" if windows else "libonnxruntime_providers_webgpu.so"
    components = [{"id": "cpu", "required": True, "version": version,
                   "description": "Application and CPU inference/correction; shared native dependencies always retained",
                   "files": files(core), "dependencies": ["vc-runtime"] if windows else ["system-libraries"],
                   "installation": "bundled", "detection": "explicit CPU worker warmup"},
                  {"id": "webgpu", "required": False, "description": "WebGPU inference; requires a usable system GPU driver",
                   "files": files([plugin]), "bundled_source": "optional-payloads/webgpu/" + plugin, "dependencies": ["cpu"], "installation": "bundled",
                   "detection": "explicit WebGPU worker warmup"}]
    if windows:
        # The DirectML ORT build can import DirectML.dll even for CPU inference.
        # Keep it in cpu's closure; optional selection controls use, not removal.
        components.append({"id": "directml", "required": False, "files": files(["DirectML.dll"]),
                           "dependencies": ["cpu"], "installation": "shared payload retained for CPU dependency closure",
                           "detection": "hardware DXGI adapter and explicit DirectML worker warmup"})
    return {"schema": 1, "rid": rid, "architecture": rid.split("-", 1)[1],
            "modes": ["recommended", "cpu", "custom"], "components": components,
            "runtime_packages": runtime.get("packages", []), "driver_installation": "official OS/vendor guidance only",
            "notices": ["runtime-licenses", "licenses/dependencies"],
            "system_support": ["Windows 11"] if windows else ["Ubuntu 24.04"],
            "debian13": "dependency and native installation verification pending"}
