"""Rewrite the pure-Python SDK wheel so every platform ships the same bytes.

0.5.0 shipped five different files named colorcorrect_studio-3.0.0-py3-none-any.whl: the
Windows builds had CRLF sources from the checkout, every build had its own timestamps, and
file modes differed (0666 on Windows, 0644 elsewhere). One name with five hashes breaks pip
caches, hash-pinned installs and lock files that move between machines.

This keeps the wheel's contents and metadata, and normalizes everything a build machine adds:
LF line endings in text files, one fixed timestamp, mode 0644, sorted entries, deflate level 9,
and a regenerated RECORD.
"""
import base64
import hashlib
import io
import sys
import zipfile
from pathlib import Path

FIXED_TIME = (1980, 1, 1, 0, 0, 0)  # The earliest time a zip entry can hold.
TEXT_SUFFIXES = {".py", ".pyi", ".txt", ".md", ".cfg", ".toml", ".json", ".typed"}
TEXT_NAMES = {"METADATA", "WHEEL", "LICENSE", "NOTICE", "RECORD", "INSTALLER", "REQUESTED"}


def _is_text(name: str) -> bool:
    leaf = name.rsplit("/", 1)[-1]
    return leaf in TEXT_NAMES or Path(leaf).suffix.lower() in TEXT_SUFFIXES


def _record_hash(data: bytes) -> str:
    return "sha256=" + base64.urlsafe_b64encode(hashlib.sha256(data).digest()).rstrip(b"=").decode("ascii")


def normalize(wheel: Path) -> str:
    """Normalize the wheel in place; return its new SHA-256."""
    with zipfile.ZipFile(wheel) as source:
        entries = {}
        for info in source.infolist():
            if info.is_dir():
                continue
            data = source.read(info)
            if _is_text(info.filename):
                data = data.replace(b"\r\n", b"\n")
            entries[info.filename] = data
    records = [name for name in entries if name.endswith(".dist-info/RECORD")]
    if len(records) != 1:
        raise ValueError(f"{wheel.name} must contain exactly one RECORD")
    record = records[0]
    lines = [f"{name},{_record_hash(data)},{len(data)}" for name, data in sorted(entries.items()) if name != record]
    entries[record] = ("\n".join([*lines, f"{record},,"]) + "\n").encode("utf-8")

    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as target:
        # RECORD goes last, as the wheel format recommends.
        for name in sorted(entries, key=lambda n: (n == record, n)):
            info = zipfile.ZipInfo(name, date_time=FIXED_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 3  # Unix, so the mode below is the one installers read.
            info.external_attr = (0o100644 & 0xFFFF) << 16
            target.writestr(info, entries[name], compresslevel=9)
    wheel.write_bytes(buffer.getvalue())
    return hashlib.sha256(buffer.getvalue()).hexdigest()


if __name__ == "__main__":
    for argument in sys.argv[1:]:
        print(normalize(Path(argument)), argument)
