"""Offline checks for maintained guides, citations, examples and demo references.

Remote URLs are syntax-checked only; this check does not confuse unavailable
networks with broken local files. Runtime tutorial execution belongs in guests.
"""
from __future__ import annotations

import argparse
import ast
import hashlib
import json
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import struct
from urllib.parse import unquote, urlsplit
import xml.etree.ElementTree as ET
import zlib

import yaml  # Pinned QA dependency; never an SDK/application runtime dependency.

GUIDES = ("README.md", "api/README.md", "docs/api/README.md", "docs/api/protocol.md",
          "docs/api/VERIFICATION.md", "docs/user-guide.md", "docs/building.md", "notebooks/README.md",
          "docs/ui-validation.md", "docs/readiness.md", "CHANGELOG.md")
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


def check_demo_manifest(path: Path) -> list[str]:
    """Bind declared artifacts to local bytes; this does not validate pixel truth."""
    def unique_keys(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"duplicate JSON key {key!r}")
            result[key] = value
        return result

    prefix = path.name + ": "
    try:
        manifest = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_keys)
    except (OSError, UnicodeError, ValueError) as error:
        return [prefix + f"invalid manifest JSON: {error}"]
    if not isinstance(manifest, dict):
        return [prefix + "expected a manifest object"]

    errors = []
    if manifest.get("source_color_space") != "srgb":
        errors.append(prefix + "source_color_space must be srgb for this demo")
    dimensions = manifest.get("dimensions")
    if (not isinstance(dimensions, list) or len(dimensions) != 2
            or any(type(value) is not int or value <= 0 for value in dimensions)):
        errors.append(prefix + "dimensions must contain positive integer width and height")
    if type(manifest.get("total_duration_ms")) is not int or manifest["total_duration_ms"] <= 0:
        errors.append(prefix + "total_duration_ms must be a positive integer")
    frames = manifest.get("frames")
    if not isinstance(frames, list) or len(frames) < 2:
        errors.append(prefix + "frames must contain at least two scene objects")
    else:
        seen = set()
        for index, frame in enumerate(frames):
            if not isinstance(frame, dict):
                errors.append(prefix + f"frame {index} must be an object")
                continue
            identity = frame.get("id")
            if not isinstance(identity, str) or not identity.strip() or identity in seen:
                errors.append(prefix + f"frame {index} needs a unique nonempty id")
            else:
                seen.add(identity)
            if type(frame.get("duration_ms")) is not int or frame["duration_ms"] <= 0:
                errors.append(prefix + f"frame {index} duration_ms must be a positive integer")

    artifacts = manifest.get("artifacts")
    if not isinstance(artifacts, dict) or not artifacts:
        return errors + [prefix + "artifacts must be a nonempty object"]
    if not {"studio-dark.png", "studio-dark.gif"} <= set(artifacts):
        errors.append(prefix + "artifacts must include the primary PNG and GIF alternative")
    root = path.parent.resolve()
    for relative, metadata in artifacts.items():
        # Interpret the format consistently on Linux and Windows, including
        # Windows drives/UNC names even when this check runs on Linux.
        if (not isinstance(relative, str) or not relative or "\\" in relative or "\x00" in relative
                or ":" in relative or PureWindowsPath(relative).drive
                or PurePosixPath(relative).is_absolute()
                or any(part in ("", ".", "..") for part in relative.split("/"))):
            errors.append(prefix + f"artifact path must be a contained relative path: {relative!r}")
            continue
        try:
            target = (root / relative).resolve()
            if not target.is_relative_to(root):
                errors.append(prefix + f"artifact path escapes the demo directory: {relative}")
                continue
            if not target.is_file():
                errors.append(prefix + f"missing artifact: {relative}")
                continue
            if not isinstance(metadata, dict):
                errors.append(prefix + f"artifact metadata must be an object: {relative}")
                continue
            size, expected = metadata.get("bytes"), metadata.get("sha256")
            if type(size) is not int or size < 0:
                errors.append(prefix + f"artifact bytes must be a nonnegative integer: {relative}")
            elif target.stat().st_size != size:
                errors.append(prefix + f"artifact byte count mismatch: {relative}")
            if not isinstance(expected, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", expected):
                errors.append(prefix + f"artifact sha256 must contain 64 hex digits: {relative}")
            else:
                digest = hashlib.sha256()
                with target.open("rb") as source:
                    for block in iter(lambda: source.read(1024 * 1024), b""):
                        digest.update(block)
                if digest.hexdigest() != expected.lower():
                    errors.append(prefix + f"artifact SHA-256 mismatch: {relative}")
        except (OSError, ValueError, RuntimeError) as error:
            errors.append(prefix + f"artifact cannot be checked: {relative}: {error}")
    return errors


def check_demo(path: Path) -> list[str]:
    if path.name in ("demo-manifest.json", "manifest.json"):
        return check_demo_manifest(path)
    if path.name == "storyboard.json":
        return check_demo_storyboard(path)
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


def check_demo_storyboard(path: Path) -> list[str]:
    """Validate the public portable replay data without loading image libraries."""
    try:
        story = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, ValueError) as error:
        return [f"{path.name}: invalid storyboard JSON: {error}"]
    if not isinstance(story, dict) or story.get("source_color_space") != "srgb":
        return [f"{path.name}: expected an sRGB storyboard object"]
    canvas = story.get("canvas")
    if canvas is not None and (not isinstance(canvas, list) or len(canvas) != 2
                               or any(type(value) is not int or value <= 0 for value in canvas)):
        return [f"{path.name}: canvas needs positive integer width and height"]
    frames = story.get("frames")
    if not isinstance(frames, list) or len(frames) < 2:
        return [f"{path.name}: expected at least two frames"]
    errors, seen, total, previous_comparison = [], set(), 0, False
    root = path.parent.resolve()
    for index, frame in enumerate(frames):
        prefix = f"{path.name}: frame {index}: "
        if not isinstance(frame, dict):
            errors.append(prefix + "expected a frame object")
            continue
        identity = frame.get("id")
        if not isinstance(identity, str) or not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,63}", identity) or identity in seen:
            errors.append(prefix + "expected a unique lowercase, hyphenated id")
        elif identity:
            seen.add(identity)
        source = frame.get("source")
        if (not isinstance(source, str) or not source or "\\" in source or "\x00" in source
                or ":" in source or PurePosixPath(source).is_absolute()
                or any(part in ("", ".", "..") for part in source.split("/"))):
            errors.append(prefix + "source must be a portable contained relative PNG path")
        else:
            try:
                target = (root / source).resolve()
                if not target.is_relative_to(root) or not target.is_file() or target.suffix.lower() != ".png":
                    errors.append(prefix + "source must name an existing contained PNG")
            except (OSError, ValueError, RuntimeError) as error:
                errors.append(prefix + f"cannot resolve source: {error}")
        if frame.get("presentation", "window") not in ("window", "dialog-closeup", "popup-closeup"):
            errors.append(prefix + "unknown presentation")
        hold, transition = frame.get("duration_ms"), frame.get("transition_ms", 0)
        if type(hold) is not int or hold < 1000 or hold % 10:
            errors.append(prefix + "duration_ms must be >=1000 and a multiple of 10")
        else:
            total += hold
        if type(transition) is not int or not 0 <= transition <= 250 or transition % 10:
            errors.append(prefix + "transition_ms must be 0-250 and a multiple of 10")
        else:
            total += transition
            if transition and (index == 0 or previous_comparison or frame.get("comparison", False)):
                errors.append(prefix + "scientific comparisons and the first frame need clean cuts")
        previous_comparison = bool(frame.get("comparison", False))
    if not 45000 <= total <= 55000:
        errors.append(f"{path.name}: total duration must be 45-55 seconds")
    return errors


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
