"""Remove private ONNX metadata paths, preserving original bytes and all computation.

Maintenance-only dependency: ONNX. No ML runtime is installed by the app or SDK.
Run explicitly with --apply; originals and the equivalence audit stay private.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil

MODELS = ("models/opt2/best_assemble_fp32_1280.onnx", "models/opt2/best_assemble_fp32_1536.onnx", "models/plane/plane_det_model_YOLO_512_n.onnx")
PREFIX = re.compile(r"[A-Za-z]:[\\/]+Users[\\/]+[^\\/\r\n]+[\\/]", re.I)


def sha(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def portable_model(source: bytes) -> tuple[bytes, dict]:
    import onnx
    original = onnx.load_model_from_string(source)
    portable = onnx.ModelProto()
    portable.CopyFrom(original)
    changed = []
    for item in portable.metadata_props:
        replacement = PREFIX.sub("/model/source/", item.value)
        if replacement != item.value:
            if item.key.lower() not in {"description", "args", "path", "data"}:
                raise ValueError(f"unreviewed private metadata key: {item.key}")
            changed.append(item.key)
            item.value = replacement
    onnx.checker.check_model(portable)
    for model in (original, portable):
        # Whole-proto comparison below covers all nested graphs/functions too.
        for tensor in model.graph.initializer:
            if tensor.data_location == onnx.TensorProto.EXTERNAL or tensor.external_data:
                raise ValueError("external detector data needs an explicit companion-file contract")
    before = onnx.ModelProto(); before.CopyFrom(original); before.ClearField("metadata_props")
    after = onnx.ModelProto(); after.CopyFrom(portable); after.ClearField("metadata_props")
    immutable = before.SerializeToString(deterministic=True)
    if immutable != after.SerializeToString(deterministic=True):
        raise ValueError("model computation changed")
    result = portable.SerializeToString() if changed else source
    if PREFIX.search(result.decode("latin1")):
        raise ValueError("private model path remains outside reviewed metadata")
    return result, {
        "original_sha256": sha(source), "portable_sha256": sha(result),
        "immutable_model_sha256": sha(immutable), "only_metadata_changed": True,
        "changed_metadata_keys": changed,
        "ir_version": original.ir_version,
        "opsets": [{"domain": item.domain, "version": item.version} for item in original.opset_import],
        "graph_sha256": sha(original.graph.SerializeToString(deterministic=True)),
        "input_signatures_sha256": [sha(item.SerializeToString(deterministic=True)) for item in original.graph.input],
        "output_signatures_sha256": [sha(item.SerializeToString(deterministic=True)) for item in original.graph.output],
        "initializers": [{"name": tensor.name, "data_type": tensor.data_type, "dimensions": list(tensor.dims),
                          "tensor_sha256": sha(tensor.SerializeToString(deterministic=True))} for tensor in original.graph.initializer],
        "external_data": False,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--backup", type=Path, required=True, help="new private directory, never overwritten")
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    root = args.root.resolve(strict=True)
    originals = {name: (root / name).read_bytes() for name in MODELS}
    candidates = {name: portable_model(value) for name, value in originals.items()}
    if args.backup.exists():
        parser.error("backup directory already exists")
    args.backup.mkdir(parents=True, exist_ok=False)
    for name, value in originals.items():
        destination = args.backup / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(value)
        if sha(destination.read_bytes()) != sha(value):
            raise ValueError("backup verification failed")
    if args.apply:
        for name, (value, _) in candidates.items():
            if (root / name).read_bytes() != originals[name]:
                raise ValueError("model changed during maintenance")
        for name, (value, _) in candidates.items():
            if value != originals[name]:
                temporary = (root / name).with_suffix(".portable.tmp")
                temporary.write_bytes(value)
                temporary.replace(root / name)
    audit = {"schema": 1, "applied": args.apply, "files": [{"path": name, **item[1]} for name, item in candidates.items()]}
    (args.backup / "metadata-equivalence.json").write_text(json.dumps(audit, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"applied": args.apply, "models": len(candidates), "metadata_changes": sum(bool(item[1]["changed_metadata_keys"]) for item in candidates.values()), "backup": str(args.backup)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
