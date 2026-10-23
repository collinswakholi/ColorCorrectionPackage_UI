"""Export current allowed source, including unstaged/untracked work, without Git changes.

Never uses `git archive HEAD`: HEAD may precede all pending implementation.
The same inventory is used by --check and export. Links are rejected, not followed.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from package_support import source_identity

ROOT_FILES = {
    ".gitattributes", ".gitignore", "CHANGELOG.md", "CITATION.cff", "CONTRIBUTING.md",
    "Directory.Build.props", "global.json", "LICENSE", "README.md", "SECURITY.md",
    "THIRD_PARTY_NOTICES.md", "rust-toolchain.toml",
}
TREES = (".github/workflows", "api", "app", "engine", "refs", "docs", "scripts", "installer", "notebooks", "licenses", "testdata/acceptance", "test_images")
IGNORED_DIRS = {".git", "__pycache__", "TestResults", ".avalonia-build-tasks", ".ipynb_checkpoints"}
FORBIDDEN_SUFFIXES = {".exe", ".dll", ".so", ".dylib", ".pdb", ".pyc", ".pfx", ".p12", ".key", ".pkl", ".torchscript"}
TEXT_SUFFIXES = {".md", ".py", ".rs", ".cs", ".json", ".ipynb", ".ps1", ".sh", ".yml", ".yaml", ".toml", ".iss", ".txt", ".props", ".csproj", ".cff", ".psd1", ".axaml"}
PRIVATE = re.compile(r"(?:[A-Za-z]:[\\/]+Users[\\/]+C[o]llins[\\/]|/Users/C[o]llins/|-----BEGIN (?:[A-Z0-9]+ )?PRIVATE KEY-----)", re.I)
REQUIRED = {"global.json", "rust-toolchain.toml", "engine/Cargo.toml", "engine/Cargo.lock", "app/ColorCorrect.sln", "api/pyproject.toml", "models/manifest.json", "models/NOTICE.md", "models/LICENSE-AGPL-3.0.txt", "testdata/acceptance/manifest.json"}


def is_link(path: Path) -> bool:
    try:
        info = path.lstat()
    except FileNotFoundError:
        return False
    # Path.is_junction is only available on Python 3.12+. Reject all Windows
    # reparse points so the source gate remains safe on supported Python 3.10.
    return stat.S_ISLNK(info.st_mode) or bool(getattr(info, "st_file_attributes", 0) & 0x400)


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        value = hashlib.sha256()
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(chunk)
        return value.hexdigest()


def inventory(root: Path) -> list[dict]:
    candidates = {root / name for name in ROOT_FILES if (root / name).is_file()}
    for relative in TREES:
        start = root / relative
        if not start.exists():
            continue
        if is_link(start):
            raise ValueError(f"source tree is a link: {relative}")
        for base, directories, files in os.walk(start, followlinks=False):
            def generated(name: str) -> bool:
                parent = Path(base)
                return (name in IGNORED_DIRS or name.startswith(".venv") or name.endswith((".egg-info", ".dist-info"))
                        or (name == "target" and (parent / "Cargo.toml").is_file())
                        or (name in {"bin", "obj"} and any(parent.glob("*.csproj")))
                        or (name == "build" and parent == root / "api"))
            directories[:] = [name for name in directories if not generated(name)]
            for name in directories:
                if is_link(Path(base, name)):
                    raise ValueError(f"source directory is a link: {Path(base, name).relative_to(root)}")
            candidates.update(Path(base, name) for name in files)
    manifest_path = root / "models/manifest.json"
    models = json.loads(manifest_path.read_text(encoding="utf-8"))
    candidates.update(root / "models" / name for name in ("manifest.json", "NOTICE.md", "LICENSE-AGPL-3.0.txt"))
    for item in models["files"]:
        name = Path(item["path"])
        if name.is_absolute() or ".." in name.parts or name.parts[0] != "models":
            raise ValueError("unsafe model manifest path")
        path = root / name
        if is_link(path) or path.stat().st_size != item["bytes"] or digest(path) != item["sha256"]:
            raise ValueError(f"model identity mismatch: {name}")
        candidates.add(path)
    result = []
    for path in sorted(candidates):
        relative = path.relative_to(root).as_posix()
        # Historical video binaries can remain locally until cleanup is approved.
        if relative in {"installer/ffmpeg.exe", "installer/ffprobe.exe"}:
            continue
        if is_link(path):
            raise ValueError(f"source file is a link: {relative}")
        if root not in path.resolve(strict=True).parents or any(is_link(parent) for parent in path.parents if parent != root and root in parent.parents):
            raise ValueError(f"source path crosses a link or escapes root: {relative}")
        if path.suffix.lower() in FORBIDDEN_SUFFIXES or path.name.startswith(".env"):
            raise ValueError(f"binary/private artifact in source: {relative}")
        if path.stat().st_size >= 100 * 1024 * 1024:
            raise ValueError(f"individual source file is too large: {relative}")
        posix_script = path.suffix.lower() == ".sh" or (relative.startswith("installer/linux/") and path.read_bytes().startswith(b"#!"))
        if posix_script and b"\r\n" in path.read_bytes():
            raise ValueError(f"POSIX script requires LF line endings in the current source: {relative}")
        if path.suffix.lower() in TEXT_SUFFIXES or posix_script:
            content = path.read_text(encoding="utf-8-sig", errors="strict")
            if PRIVATE.search(content):
                raise ValueError(f"private path/key in source: {relative}")
            if path.suffix.lower() == ".ipynb":
                if any(cell.get("outputs") or cell.get("execution_count") is not None for cell in json.loads(content)["cells"]):
                    raise ValueError(f"executed notebook in source: {relative}")
        result.append({"path": relative, "bytes": path.stat().st_size, "sha256": digest(path)})
    missing = REQUIRED - {item["path"] for item in result}
    if missing:
        raise ValueError("required source missing: " + ", ".join(sorted(missing)))
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--output", type=Path, help="new directory; existing outputs are never replaced")
    parser.add_argument("--check", action="store_true", help="validate without writing any source or metadata")
    args = parser.parse_args()
    root = args.root.resolve(strict=True)
    files = inventory(root)
    identity = source_identity(root)
    report = {"schema": 1, "kind": "current-source-export", "base_commit": identity["git_commit"],
              "working_tree_dirty": identity["working_tree_dirty"], "source": identity, "files": files}
    if not args.check:
        if args.output is None:
            parser.error("--output or --check is required")
        output = args.output.absolute()
        if output.exists() or is_link(output):
            parser.error("output must be a new directory")
        if any(is_link(parent) for parent in output.parents):
            parser.error("output parent must not be a link")
        if output == root or any(output == root / tree or root / tree in output.parents for tree in (*TREES, "models")):
            parser.error("output cannot be inside public source directories")
        output.mkdir(parents=True, exist_ok=False)
        for item in files:
            destination = output / item["path"]
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(root / item["path"], destination, follow_symlinks=False)
            if digest(destination) != item["sha256"]:
                raise ValueError(f"source changed during export: {item['path']}")
        (output / "source-manifest.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": "passed", "files": len(files), "bytes": sum(item["bytes"] for item in files), "output": str(args.output) if not args.check else None}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
