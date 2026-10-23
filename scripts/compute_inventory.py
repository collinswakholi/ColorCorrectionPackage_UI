"""Capture benchmark hardware/runtime/graph facts without changing app settings."""
from __future__ import annotations
import argparse
from collections import Counter
import ctypes
import json
import os
from pathlib import Path
import platform


def windows_hardware() -> dict:
    import winreg
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"HARDWARE\DESCRIPTION\System\CentralProcessor\0") as key:
        cpu = winreg.QueryValueEx(key, "ProcessorNameString")[0]
    class Guid(ctypes.Structure):
        _fields_ = [("a", ctypes.c_uint32), ("b", ctypes.c_uint16), ("c", ctypes.c_uint16), ("d", ctypes.c_ubyte * 8)]
    class Luid(ctypes.Structure):
        _fields_ = [("low", ctypes.c_uint32), ("high", ctypes.c_int32)]
    class Description(ctypes.Structure):
        _fields_ = [("description", ctypes.c_wchar * 128), ("vendor", ctypes.c_uint32),
                    ("device", ctypes.c_uint32), ("subsystem", ctypes.c_uint32), ("revision", ctypes.c_uint32),
                    ("video_memory", ctypes.c_size_t), ("system_memory", ctypes.c_size_t),
                    ("shared_memory", ctypes.c_size_t), ("luid", Luid), ("flags", ctypes.c_uint32)]
    factory = ctypes.c_void_p()
    iid = Guid(0x770AAE78, 0xF26F, 0x4DBA, (ctypes.c_ubyte * 8)(0xA8, 0x29, 0x25, 0x3C, 0x83, 0xD1, 0xB3, 0x87))
    # IID_IDXGIFactory1 = 770aae78-f26f-4dba-a829-253c83d1b387.
    create = ctypes.WinDLL("dxgi").CreateDXGIFactory1
    create.argtypes = [ctypes.POINTER(Guid), ctypes.POINTER(ctypes.c_void_p)]
    create.restype = ctypes.c_int32
    if create(ctypes.byref(iid), ctypes.byref(factory)) < 0:
        return {"cpu": cpu, "dxgi_error": "factory creation failed"}
    def method(obj, index, result, *arguments):
        table = ctypes.cast(obj, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
        return ctypes.WINFUNCTYPE(result, ctypes.c_void_p, *arguments)(table[index])
    adapters = []
    try:
        for index in range(32):
            adapter = ctypes.c_void_p()
            if method(factory, 12, ctypes.c_int32, ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p))(
                    factory, index, ctypes.byref(adapter)) < 0:
                break
            try:
                description = Description()
                if method(adapter, 10, ctypes.c_int32, ctypes.POINTER(Description))(
                        adapter, ctypes.byref(description)) >= 0:
                    adapters.append({"dxgi_index": index, "name": description.description,
                                     "vendor_id": description.vendor, "device_id": description.device,
                                     "dedicated_bytes": description.video_memory, "flags": description.flags,
                                     "luid_high": description.luid.high, "luid_low": description.luid.low,
                                     "luid_hex": f"0x{description.luid.high & 0xffffffff:08x}_0x{description.luid.low:08x}"})
            finally:
                method(adapter, 2, ctypes.c_uint32)(adapter)
    finally:
        method(factory, 2, ctypes.c_uint32)(factory)
    return {"cpu": cpu, "dxgi_adapters": adapters}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    facts = {"platform": platform.platform(), "hardware": windows_hardware() if os.name == "nt" else {}}
    try:
        import onnx
        graphs = {}
        for path in (args.root / "models").rglob("*.onnx"):
            model = onnx.load(str(path), load_external_data=False)
            graphs[str(path.relative_to(args.root))] = {
                "ir": model.ir_version, "opsets": [{x.domain: x.version} for x in model.opset_import],
                "operators": dict(Counter(node.op_type for node in model.graph.node)),
                "inputs": [{"name": x.name, "shape": [d.dim_value or d.dim_param for d in x.type.tensor_type.shape.dim]} for x in model.graph.input]}
        facts["onnx_graphs"] = graphs
    except ImportError:
        facts["onnx_graphs"] = "onnx inspection package unavailable"
    args.output.write_text(json.dumps(facts, indent=2), encoding="utf-8")
    print(args.output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
