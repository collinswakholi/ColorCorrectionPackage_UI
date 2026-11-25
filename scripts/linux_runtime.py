"""Linux runtime dependencies: one source of truth for check-system, install-dependencies and the .deb.

``installer/linux/runtime-dependencies`` is a plain POSIX-shell file of single-quoted
assignments, so the shell scripts can source it and this module can parse it.
"""
from __future__ import annotations

import re
import struct
from pathlib import Path

RUNTIME_DEPENDENCIES = Path(__file__).resolve().parents[1] / "installer/linux/runtime-dependencies"
_ASSIGNMENT = re.compile(r"^([A-Z][A-Za-z0-9_]*)='([^']*)'", re.MULTILINE)
_SHT_GNU_VERNEED = 0x6FFFFFFE
_VER_FLG_WEAK = 0x2


def read_runtime_dependencies(path: Path = RUNTIME_DEPENDENCIES) -> dict[str, str]:
    """Return the file's assignments. Values are taken literally (the file has no expansions)."""
    text = path.read_text(encoding="utf-8")
    values = {name: value for name, value in _ASSIGNMENT.findall(text)}
    for required in ("CCP_REQUIRED_LIBS", "CCP_SUPPORTED_DISTROS", "CCP_PACKAGES_COMMON", "CCP_GLIBC_MINIMUM"):
        if required not in values:
            raise ValueError(f"{path.name} is missing {required}")
    for distro in values["CCP_SUPPORTED_DISTROS"].split():
        for prefix in ("CCP_PACKAGES_", "CCP_DISTRO_NAME_"):
            if prefix + distro not in values:
                raise ValueError(f"{path.name} lists {distro} but has no {prefix}{distro}")
    return values


def library_entries(values: dict[str, str], key: str = "CCP_REQUIRED_LIBS") -> list[tuple[str, str]]:
    """(soname, purpose) pairs from a CCP_*_LIBS block."""
    entries = []
    for line in values.get(key, "").splitlines():
        line = line.strip()
        if line:
            soname, _, purpose = line.partition("|")
            entries.append((soname, purpose))
    return entries


def version_tuple(version: str) -> tuple[int, ...]:
    return tuple(int(part) for part in version.split("."))


def glibc_requirements(path: Path) -> set[str]:
    """Non-weak GLIBC_x.y symbol versions an ELF file requires (from .gnu.version_r)."""
    data = path.read_bytes()
    # Only complete 64-bit ELF files carry a usable version table; ldd inspects the rest.
    if data[:4] != b"\x7fELF" or len(data) < 64 or data[4] != 2:
        return set()
    try:
        return _verneed_glibc(data)
    except (struct.error, IndexError, ValueError):
        return set()


def _verneed_glibc(data: bytes) -> set[str]:
    endian = "<" if data[5] == 1 else ">"
    shoff = struct.unpack_from(endian + "Q", data, 0x28)[0]
    shentsize, shnum = struct.unpack_from(endian + "HH", data, 0x3A)
    sections = [struct.unpack_from(endian + "IIQQQQIIQQ", data, shoff + i * shentsize) for i in range(shnum)]
    needs: set[str] = set()
    for _name, kind, _flags, _addr, offset, _size, link, info, _align, _entsize in sections:
        if kind != _SHT_GNU_VERNEED:
            continue
        strtab = sections[link][4]

        def string(at: int) -> str:
            start = strtab + at
            return data[start:data.index(0, start)].decode("ascii")

        entry = offset
        for _ in range(info):
            _version, count, _file, aux, following = struct.unpack_from(endian + "HHIII", data, entry)
            at = entry + aux
            for _ in range(count):
                _hash, flags, _other, name, next_aux = struct.unpack_from(endian + "IHHII", data, at)
                label = string(name)
                if label.startswith("GLIBC_") and label[6:7].isdigit() and not flags & _VER_FLG_WEAK:
                    needs.add(label[6:])
                at += next_aux
            if not following:
                break
            entry += following
    return needs


def payload_dependencies(payload: Path) -> dict[str, str]:
    """The payload's runtime-dependencies (packaging copies it in), else the repository copy."""
    shipped = payload / "runtime-dependencies"
    return read_runtime_dependencies(shipped if shipped.is_file() else RUNTIME_DEPENDENCIES)


def glibc_floor_violations(payload: Path, minimum: str) -> list[str]:
    """Payload ELF files that need a newer glibc than ``minimum``."""
    floor = version_tuple(minimum)
    problems = []
    for path in sorted(payload.rglob("*")):
        if not path.is_file() or path.is_symlink():
            continue
        with path.open("rb") as stream:
            if stream.read(4) != b"\x7fELF":
                continue
        needs = glibc_requirements(path)
        newest = max(needs, key=version_tuple, default=None)
        if newest and version_tuple(newest) > floor:
            problems.append(f"{path.relative_to(payload).as_posix()} needs GLIBC_{newest} (floor {minimum})")
    return problems


def deb_depends(values: dict[str, str], extra: tuple[str, ...] = ()) -> list[str]:
    """Depends entries that resolve on every supported distribution.

    Distro-specific lists are aligned by position (ICU, OpenSSL, GTK, GLib) and
    joined as alternatives, so one .deb installs on Ubuntu 24.04/22.04 and Debian 13/12.
    """
    distros = values["CCP_SUPPORTED_DISTROS"].split()
    columns = [values["CCP_PACKAGES_" + distro].split() for distro in distros]
    if len({len(column) for column in columns}) != 1:
        raise ValueError("Per-distribution package lists must have the same length and order")
    depends = [f"libc6 (>= {values['CCP_GLIBC_MINIMUM']})"]
    depends += [package for package in values["CCP_PACKAGES_COMMON"].split() if package != "libc6"]
    for alternatives in zip(*columns):
        unique = list(dict.fromkeys(alternatives))
        depends.append(" | ".join(unique))
    depends += list(extra)
    return depends
