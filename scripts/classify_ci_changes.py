"""Skip native builds only for a complete, known README/demo-only change set.

Uses Git rather than truncated webhook file lists or a third-party action. Missing
history and unfamiliar events fail closed to full native validation.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess

DEMO_DIRECTORY = "docs/images/demo/"
DEMO_EXTENSIONS = {".png", ".apng", ".gif", ".webp", ".svg", ".md"}


def lightweight_path(path: str) -> bool:
    """Deliberately do not exempt bundled documentation or all Markdown files."""
    parts = PurePosixPath(path).parts
    if not path or path.startswith("/") or "\\" in path or any(p in (".", "..") for p in parts):
        return False
    return path == "README.md" or (
        path.startswith(DEMO_DIRECTORY)
        and (PurePosixPath(path).suffix.lower() in DEMO_EXTENSIONS
             or path in (DEMO_DIRECTORY + "manifest.json", DEMO_DIRECTORY + "demo-manifest.json",
                         DEMO_DIRECTORY + "storyboard.json"))
    )


def classify_paths(paths: list[str] | None) -> dict:
    native = not paths or any(not lightweight_path(path) for path in paths)
    return {"native": native, "reason": "native inputs or unavailable change set" if native
            else "README/demo-only change", "paths": paths or []}


def changed_paths(root: Path, base: str, head: str, *, merge_base: bool = False) -> list[str]:
    for revision in (base, head):
        if not re.fullmatch(r"[0-9a-fA-F]{40,64}", revision) or set(revision) == {"0"}:
            raise ValueError("Missing immutable comparison revision")
    comparison = f"{base}...{head}" if merge_base else f"{base}..{head}"
    result = subprocess.run(["git", "diff", "--name-status", "-z", "--find-renames", comparison, "--"],
                            cwd=root, check=True, capture_output=True)
    tokens = result.stdout.decode("utf-8", errors="strict").split("\0")
    paths, index = [], 0
    while index < len(tokens) and tokens[index]:
        status = tokens[index]
        index += 1
        count = 2 if status.startswith(("R", "C")) else 1
        if not re.fullmatch(r"[ACDMRTUXB][0-9]*", status) or index + count > len(tokens):
            raise ValueError("Unrecognized Git change record")
        for path in tokens[index:index + count]:
            if not path:
                raise ValueError("Incomplete Git change record")
            paths.append(path)
        index += count
    return list(dict.fromkeys(paths))


def classify_event(root: Path, event_name: str, reference: str, event: dict) -> dict:
    if event_name == "workflow_dispatch" or reference.startswith("refs/tags/"):
        return {"native": True, "reason": "manual or tag validation", "paths": []}
    try:
        if event_name == "push" and reference == "refs/heads/main":
            paths = changed_paths(root, event.get("before", ""), event.get("after", ""))
        elif event_name == "pull_request":
            pr = event["pull_request"]
            paths = changed_paths(root, pr["base"]["sha"], pr["head"]["sha"], merge_base=True)
        else:
            return classify_paths(None)
        return classify_paths(paths)
    except (KeyError, ValueError, UnicodeError, OSError, subprocess.CalledProcessError):
        return classify_paths(None)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    try:
        event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))
    except (KeyError, OSError, ValueError):
        event = {}
    result = classify_event(args.root, os.environ.get("GITHUB_EVENT_NAME", ""),
                            os.environ.get("GITHUB_REF", ""), event)
    print(json.dumps(result, ensure_ascii=True))
    if output := os.environ.get("GITHUB_OUTPUT"):
        with open(output, "a", encoding="utf-8") as destination:
            destination.write("native=" + str(result["native"]).lower() + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
