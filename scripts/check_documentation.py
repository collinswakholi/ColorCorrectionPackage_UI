"""Offline checks for maintained guides, citations, examples and demo references.

Remote URLs are syntax-checked only; this check does not confuse unavailable
networks with broken local files. Runtime tutorial execution belongs in guests.
"""
from __future__ import annotations

import argparse
import ast
import json
from pathlib import Path
import re
import struct
from urllib.parse import unquote, urlsplit
import xml.etree.ElementTree as ET
import zlib

import yaml  # Pinned QA dependency; never an SDK/application runtime dependency.

GUIDES = ("README.md", "api/README.md", "docs/api/README.md", "docs/api/protocol.md",
          "docs/user-guide.md", "docs/building.md", "docs/macos-install.md", "notebooks/README.md",
          "CHANGELOG.md", "docs/native-validation.md", "docs/releasing.md",
          "docs/compatibility.md", "docs/compute/README.md", "docs/implementation.md",
          "docs/development/README.md", "installer/README.md")
LINK = re.compile(r"!?\[[^\]\n]*\]\((<[^>]+>|[^\s)]+)(?:\s+\"[^\"]*\")?\)")
FENCE = re.compile(r"^```([^\n]*)\n(.*?)^```\s*$", re.MULTILINE | re.DOTALL)


def anchors(text: str) -> set[str]:
    found, counts = set(), {}
    for line in text.splitlines():
        if not re.match(r"^#{1,6}\s+", line):
            continue
        title = re.sub(r"^#{1,6}\s+|\s+#+\s*$", "", line).strip().lower()
        title = re.sub(r"!?\[([^\]]+)\]\([^)]*\)", r"\1", title)
        slug = "".join(ch for ch in title if ch.isalnum() or ch in " _-").replace(" ", "-")
        count = counts.get(slug, 0)
        counts[slug] = count + 1
        found.add(slug if count == 0 else f"{slug}-{count}")
    found.update(re.findall(r'<a\s+(?:name|id)=[\"\']([^\"\']+)', text))
    return found


def check_markdown(path: Path, root: Path) -> list[str]:
    # Compare canonical paths on both sides: macOS /var and Windows temporary
    # directory aliases otherwise make an internal link appear to escape.
    root, path = root.resolve(), path.resolve()
    if not path.is_relative_to(root):
        return [f"{path.name}: guide escapes repository"]
    errors = []
    text = path.read_text(encoding="utf-8")
    # Catch the observed Windows stdin encoding regression before garbled study
    # symbols or release headings reach the rendered documentation.
    if any(token in text for token in ("\u00e2\u20ac", "\u00ce\u201d", "\u00c2\u00b3", "\ufffd")):
        errors.append(f"{path.relative_to(root)}: possible UTF-8/Windows-1252 mojibake or replacement characters")
    for number, match in enumerate(FENCE.finditer(text), 1):
        if match[1].strip().lower() in ("python", "py", "python3"):
            try:
                ast.parse(match[2], filename=f"{path.name}:example-{number}")
            except SyntaxError as error:
                errors.append(f"{path.relative_to(root)}: invalid Python example: {error.msg}")
    # Ignore links displayed literally inside fenced code blocks.
    prose = FENCE.sub("", text)
    for match in LINK.finditer(prose):
        link = match[1].strip("<>")
        parsed = urlsplit(link)
        if parsed.scheme or parsed.netloc:
            if parsed.scheme in ("http", "https") and not parsed.netloc:
                errors.append(f"{path.relative_to(root)}: malformed remote URL {link}")
            continue
        target = (root / unquote(parsed.path).lstrip("/") if parsed.path.startswith("/")
                  else path.parent / unquote(parsed.path)) if parsed.path else path
        target = target.resolve()
        if not target.is_relative_to(root):
            errors.append(f"{path.relative_to(root)}: local link escapes repository: {link}")
        elif not target.exists():
            errors.append(f"{path.relative_to(root)}: missing local target: {link}")
        elif parsed.fragment and target.suffix.lower() == ".md":
            if unquote(parsed.fragment) not in anchors(target.read_text(encoding="utf-8")):
                errors.append(f"{path.relative_to(root)}: missing heading: {link}")
    return errors


def check_citation(path: Path, expected_version: str | None = None) -> list[str]:
    errors = []
    data = yaml.safe_load(path.read_text(encoding="utf-8"))
    if not isinstance(data, dict):
        return ["CITATION.cff: expected a mapping"]
    for key in ("cff-version", "message", "title", "version", "repository-code", "authors"):
        if not data.get(key):
            errors.append(f"CITATION.cff: missing {key}")
    if data.get("cff-version") != "1.2.0" or data.get("type") != "software":
        errors.append("CITATION.cff: expected CFF 1.2.0 software citation")
    if expected_version is not None and str(data.get("version")) != expected_version:
        errors.append("CITATION.cff: version differs from the native application")
    if not any(isinstance(item, dict) and item.get("doi") == "10.1002/ppj2.70067"
               for item in data.get("references", [])):
        errors.append("CITATION.cff: missing original study DOI")
    return errors


def check_demo(path: Path) -> list[str]:
    """Check demo media signatures and PNG/APNG chunk structure."""
    data = path.read_bytes()
    if path.suffix.lower() in (".png", ".apng"):
        if data[:8] != b"\x89PNG\r\n\x1a\n":
            return [f"{path.name}: invalid PNG signature"]
        position, kinds = 8, []
        frames = 0
        while position < len(data):
            if position + 12 > len(data):
                return [f"{path.name}: truncated PNG chunk"]
            size = struct.unpack_from(">I", data, position)[0]
            end = position + 12 + size
            if end > len(data):
                return [f"{path.name}: truncated PNG data"]
            kind = data[position + 4:position + 8]
            body = data[position + 8:position + 8 + size]
            checksum = struct.unpack_from(">I", data, position + 8 + size)[0]
            if zlib.crc32(kind + body) != checksum:
                return [f"{path.name}: PNG checksum mismatch"]
            kinds.append(kind)
            if kind == b"acTL":
                if size != 8:
                    return [f"{path.name}: invalid APNG animation control"]
                frames = struct.unpack_from(">I", body)[0]
            position = end
        if not kinds or kinds[0] != b"IHDR" or kinds[-1] != b"IEND" or b"IDAT" not in kinds:
            return [f"{path.name}: incomplete PNG image"]
        if b"acTL" in kinds and (frames < 2 or kinds.count(b"fcTL") != frames):
            return [f"{path.name}: inconsistent APNG frame count"]
        if path.suffix.lower() == ".apng" and b"acTL" not in kinds:
            return [f"{path.name}: APNG has no animation control chunk"]
    elif path.suffix.lower() == ".gif" and data[:6] not in (b"GIF87a", b"GIF89a"):
        return [f"{path.name}: invalid GIF signature"]
    elif path.suffix.lower() == ".svg":
        try:
            ET.fromstring(data)
        except ET.ParseError:
            return [f"{path.name}: malformed SVG"]
    return []


def check(root: Path) -> list[str]:
    errors = []
    for name in GUIDES:
        path = root / name
        if not path.is_file():
            errors.append(f"Missing maintained guide: {name}")
        else:
            errors.extend(check_markdown(path, root))
    version = ET.parse(root / "app/ColorCorrect.App/ColorCorrect.App.csproj").findtext(".//Version")
    errors.extend(check_citation(root / "CITATION.cff", version))
    for path in sorted((root / "notebooks").glob("*.ipynb")):
        notebook = json.loads(path.read_text(encoding="utf-8"))
        if notebook.get("nbformat") != 4 or not isinstance(notebook.get("cells"), list):
            errors.append(f"{path.name}: expected notebook v4 cells")
            continue
        for index, cell in enumerate(notebook["cells"]):
            if cell.get("cell_type") == "code":
                try:
                    ast.parse("".join(cell["source"]), filename=f"{path.name}:cell-{index}")
                except SyntaxError as error:
                    errors.append(f"{path.name}: cell {index}: {error.msg}")
    demo = root / "docs/images/demo"
    if demo.is_dir():
        for path in sorted(demo.rglob("*")):
            if path.is_file():
                if path.suffix.lower() == ".md":
                    errors.extend(check_markdown(path, root))
                else:
                    errors.extend(check_demo(path))
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    errors = check(args.root.resolve())
    for error in errors:
        print(error)
    print(f"Documentation: {len(errors)} errors; remote links were not fetched; runtime examples require guest validation.")
    return bool(errors)


if __name__ == "__main__":
    raise SystemExit(main())
