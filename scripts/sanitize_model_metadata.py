"""Remove local export/training paths without modifying graph or tensor data.

Maintenance tooling only: requires onnx; it is not an application dependency.
TorchScript debug pickles are never deserialized. Their path prefixes are replaced
with equal-length portable markers; all inference code/storage entries must stay
byte-identical. Original supplied files remain recoverable in CCP_New_UI/history.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import zipfile

USER_PREFIX = re.compile(rb"[A-Za-z]:[\\/]+Users[\\/]+[^\\/\x00\r\n]+[\\/]", re.I)
TEXT_PREFIX = re.compile(r"[A-Za-z]:[\\/]+Users[\\/]+[^\\/\r\n]+[\\/]", re.I)


def redact_bytes(value):
    def marker(match):
        size = len(match[0])
        replacement = b"/model/source/"
        assert size >= len(replacement)
        return replacement + b"_" * (size - len(replacement))
    return USER_PREFIX.sub(marker, value)


def redact_config(value):
    if isinstance(value, str):
        return TEXT_PREFIX.sub("/model/source/", value)
    if isinstance(value, list):
        return [redact_config(item) for item in value]
    if isinstance(value, dict):
        return {key: redact_config(item) for key, item in value.items()}
    return value


def sanitize_torchscript(source, destination):
    with zipfile.ZipFile(source) as original:
        entries = []
        immutable = {}
        for info in original.infolist():
            value = original.read(info)
            if info.filename.endswith(".debug_pkl"):
                value = redact_bytes(value)
            elif info.filename.endswith("/extra/config.txt"):
                config = json.loads(value)
                value = json.dumps(redact_config(config), separators=(",", ":")).encode()
                assert json.loads(value) == redact_config(config)
            else:
                # Includes all data, constants, bytecode, generated code and IDs.
                immutable[info.filename] = hashlib.sha256(value).hexdigest()
            entries.append((info, value))
    with zipfile.ZipFile(destination, "w") as output:
        for info, value in entries:
            output.writestr(info, value)
    with zipfile.ZipFile(destination) as output:
        for name, expected in immutable.items():
            assert hashlib.sha256(output.read(name)).hexdigest() == expected, name
        assert not any(USER_PREFIX.search(output.read(name)) for name in output.namelist())
    return len(immutable)


def sanitize_onnx(source, destination):
    import onnx
    original = onnx.load_model(source)
    portable = onnx.ModelProto()
    portable.CopyFrom(original)
    for metadata in portable.metadata_props:
        metadata.value = TEXT_PREFIX.sub("/model/source/", metadata.value)
    onnx.checker.check_model(portable)
    onnx.save_model(portable, destination)
    reloaded = onnx.load_model(destination)
    original.ClearField("metadata_props")
    reloaded.ClearField("metadata_props")
    assert original.SerializeToString(deterministic=True) == reloaded.SerializeToString(deterministic=True)
    assert not USER_PREFIX.search(destination.read_bytes())
    return len(portable.graph.initializer)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()
    evidence = []
    for name in ("best_assemble_fp16_1280.torchscript", "best_assemble_fp16_1536.torchscript",
                 "best_assemble_fp32_1280.onnx", "best_assemble_fp32_1536.onnx"):
        source, destination = args.source_root / name, args.output_root / name
        if source.resolve() == destination.resolve():
            raise ValueError("Use a separate original source folder; in-place redaction is not allowed.")
        before = hashlib.sha256(source.read_bytes()).hexdigest()
        count = (sanitize_onnx if source.suffix == ".onnx" else sanitize_torchscript)(source, destination)
        evidence.append({"path": name, "original_sha256": before,
                         "sanitized_sha256": hashlib.sha256(destination.read_bytes()).hexdigest(),
                         "immutable_entries_or_tensors_checked": count})
    print(json.dumps(evidence, indent=2))


if __name__ == "__main__":
    main()
