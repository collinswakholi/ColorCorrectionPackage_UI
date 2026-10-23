"""Inventory reproducible caches only. This command never deletes or moves files.

Unique diagnostics, research, private images and tested packages remain local.
Review this manifest after final validation before any separate cleanup action.
"""
from __future__ import annotations
import argparse
import json
import os
import stat
from pathlib import Path

CACHE_NAMES = {"__pycache__", ".avalonia-build-tasks", ".ipynb_checkpoints"}


def linked(path: Path) -> bool:
    try:
        info = path.lstat()
    except FileNotFoundError:
        return False
    return stat.S_ISLNK(info.st_mode) or bool(getattr(info, "st_file_attributes", 0) & 0x400)


def measure(path: Path) -> tuple[int, int]:
    count = size = 0
    for base, directories, files in os.walk(path, followlinks=False):
        if any(linked(Path(base, name)) for name in directories + files):
            raise ValueError(f"refusing cache with a link/reparse point: {path}")
        for name in files:
            count += 1
            size += Path(base, name).stat().st_size
    return count, size


def inventory(root: Path) -> dict:
    candidates = []
    for tree in ("app", "engine", "api", "scripts", "experiments"):
        start = root / tree
        if linked(start):
            raise ValueError(f"refusing linked tree: {tree}")
        for base, directories, _ in os.walk(start, followlinks=False):
            remaining = []
            for name in directories:
                path = Path(base, name)
                if linked(path):
                    continue
                if name == "TestResults":
                    # Test reports and captured images are evidence, not merely
                    # reproducible compiler output. Keep them and their contents.
                    continue
                # A Rust src/bin directory is source, not a .NET build cache.
                # Only recognize caches next to the manifest that produces them.
                project_parent = path.parent
                is_cache = (name in CACHE_NAMES
                            or (name == "target" and (project_parent / "Cargo.toml").is_file())
                            or (name in {"bin", "obj"} and any(project_parent.glob("*.csproj")))
                            or (tree == "api" and project_parent == root / "api" and (name == "build" or name.endswith(".egg-info"))))
                if is_cache:
                    resolved = path.resolve(strict=True)
                    if root not in resolved.parents:
                        raise ValueError("cache escapes workspace")
                    count, size = measure(path)
                    candidates.append({"path": path.relative_to(root).as_posix(), "files": count, "bytes": size, "reason": "reproducible build/test cache"})
                else:
                    remaining.append(name)
            directories[:] = remaining
    return {"schema": 1, "action": "dry-run-only", "requires_final_validation": True,
            "preserved": ["_implementation/ (all unique evidence and snapshots)", "TestResults/ reports and captured images", "experiments/ source", "dist/ tested packages", "assets/test_images and testdata/samples junctions and targets", "testdata/ccp-session.json", "all regression tests and reviewed fixtures"],
            "candidates": candidates, "total_bytes": sum(item["bytes"] for item in candidates)}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    report = inventory(args.root.resolve(strict=True))
    value = json.dumps(report, indent=2) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with args.output.open("x", encoding="utf-8") as stream:
            stream.write(value)
    else:
        print(value, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
