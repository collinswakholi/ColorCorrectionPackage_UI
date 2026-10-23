"""Collect notices from actual published NuGet and active Rust dependencies.

Reads restored package sources. --fetch-upstream explicitly permits fetching
missing license texts from the exact repository commits recorded in those
packages. No application, runtime, driver, or global setting is changed.
"""
from __future__ import annotations
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import re
import subprocess
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from package_native import TARGETS
from package_support import digest

LEGAL = re.compile(r"^(?:licen[cs]e|copying|copyright|notice|third.?party.?notices|ofl)(?:[.\-_].*)?$", re.I)
# These exact package and upstream revisions contain declarations but no full
# license file. Retain declarations/authors and supply the declared standard text.
STANDARD_TEXT = {("buildid", "1.0.5"): "MPL-2.0", ("utf8-chars", "3.0.7"): "Apache-2.0"}
SPDX_COMMIT = "d46e94e2c78ceede1cfc63cfa0396472d2798d4c"


def github(repository: str | None) -> str | None:
    match = re.match(r"https://github\.com/([^/]+/[^/#]+)", repository or "")
    return match.group(1).removesuffix(".git") if match else None


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def vendored_source(root: Path, package: dict, folder: Path) -> dict | None:
    """Recognize explicitly inventoried third-party path patches, not workspace crates."""
    vendor = root.resolve() / "engine/vendor"
    folder = folder.absolute()
    if not folder.is_relative_to(vendor):
        return None
    expected = vendor / (package["name"] + "-" + package["version"])
    if folder != expected or folder.resolve(strict=True) != expected:
        raise ValueError("Vendored dependency must be contained without links: " + str(folder))
    provenance = read_json(folder / "upstream-sha256.json")
    upstream = provenance.get("package", {})
    vcs = read_json(folder / ".cargo_vcs_info.json")
    commit = vcs.get("git", {}).get("sha1")
    if (provenance.get("schema") != 1 or upstream.get("name") != package["name"]
            or upstream.get("version") != package["version"]
            or not re.fullmatch(r"[0-9a-f]{40}", commit or "")
            or upstream.get("git_commit") != commit
            or not re.fullmatch(r"[0-9a-f]{64}", upstream.get("registry_archive_sha256", ""))
            or not github(package.get("repository"))):
        raise ValueError("Invalid vendored dependency provenance: " + str(folder))
    originals = provenance.get("files")
    if not isinstance(originals, dict) or not originals:
        raise ValueError("Missing vendored upstream source inventory: " + str(folder))
    for name, sha in originals.items():
        if (not isinstance(name, str) or "\\" in name or ":" in name
                or name.startswith("/") or any(part in ("", ".", "..") for part in name.split("/"))
                or not isinstance(sha, str) or not re.fullmatch(r"[0-9a-f]{64}", sha)):
            raise ValueError("Invalid vendored upstream source inventory: " + str(folder))
    files = {}
    for path in folder.rglob("*"):
        if path.resolve(strict=True) != path.absolute():
            raise ValueError("Vendored dependency must be contained without links: " + str(path))
        if path.is_file():
            files[path.relative_to(folder).as_posix()] = path
    if set(files) != set(originals) | {"CCP-PATCH.md", "upstream-sha256.json"}:
        raise ValueError("Untracked or missing vendored source files: " + str(folder))
    if not any(LEGAL.match(Path(name).name) for name in originals):
        raise ValueError("Missing vendored upstream license text: " + str(folder))
    for name, sha in originals.items():
        immutable = LEGAL.match(Path(name).name) or name in ("Cargo.toml", "Cargo.toml.orig", ".cargo_vcs_info.json")
        if immutable and digest(files[name]) != sha:
            raise ValueError("Changed vendored upstream license or package identity: " + name)
    if not (folder / "CCP-PATCH.md").read_text(encoding="utf-8").strip():
        raise ValueError("Vendored dependency must document local changes: " + str(folder))
    return {"path": folder.relative_to(root.resolve()).as_posix(), "upstream": provenance,
            "source_hashes": {path.relative_to(root.resolve()).as_posix(): digest(path)
                              for path in files.values()}}


def validate_notices(folder: Path, rid: str, root: Path, published: Path | None = None) -> dict:
    report = read_json(folder / "manifest.json")
    if report.get("rid") != rid or report.get("unresolved"):
        raise ValueError("Dependency notices are incomplete or for another architecture")
    for name, expected in report["source_hashes"].items():
        if digest(root / name) != expected:
            raise ValueError("Dependency notices refer to a different source: " + name)
    if published and report["published_deps_sha256"] != digest(published / "ColorCorrect.App.deps.json"):
        raise ValueError("Dependency notices do not match the published application")
    actual = {path.relative_to(folder).as_posix() for path in folder.rglob("*") if path.is_file()}
    if actual - set(report["sha256"]) - {"manifest.json"}:
        raise ValueError("Untracked files in dependency notice inventory")
    for name, expected in report["sha256"].items():
        path = (folder / name).resolve(strict=True)
        if not path.is_relative_to(folder.resolve()) or digest(path) != expected:
            raise ValueError("Dependency notice checksum mismatch: " + name)
    return report


def collect(root: Path, rid: str, published: Path, output: Path, cache: Path,
            fetch: bool = False, metadata_file: Path | None = None) -> dict:
    output.mkdir(parents=True, exist_ok=False)
    cache.mkdir(parents=True, exist_ok=True)
    sources = []
    command = ["cargo", "metadata", "--manifest-path", str(root / "engine/Cargo.toml"), "--locked", "--format-version", "1"]
    if not fetch:
        command.append("--offline")
    # Cargo emits UTF-8, including package paths and author names. Windows' ANSI
    # code page is unrelated to this protocol; keep malformed output an error.
    metadata = read_json(metadata_file) if metadata_file else json.loads(
        subprocess.check_output(command, text=True, encoding="utf-8", errors="strict"))
    tree = subprocess.check_output(["cargo", "tree", "--manifest-path", str(root / "engine/Cargo.toml"), "--locked", "--offline",
        "-p", "ccp_bridge", "--target", TARGETS[rid][2], "--edges", "normal,build", "--prefix", "none", "--format", "{p}"],
        text=True, encoding="utf-8", errors="strict")
    active = {tuple(line.split()[:2]) for line in tree.splitlines() if len(line.split()) >= 2}
    vendor_hashes = {}
    vcs_repositories = {}
    for package in metadata["packages"]:
        folder = Path(package["manifest_path"]).parent
        vcs = read_json(folder / ".cargo_vcs_info.json") if (folder / ".cargo_vcs_info.json").exists() else {}
        commit = vcs.get("git", {}).get("sha1")
        repository = github(package.get("repository")) or github(package.get("homepage"))
        if commit and repository:
            vcs_repositories[commit] = repository
    for package in metadata["packages"]:
        if (package["name"], "v" + package["version"]) not in active:
            continue
        folder = Path(package["manifest_path"]).parent
        vendor = vendored_source(root, package, folder) if package["source"] is None else None
        if package["source"] is None and vendor is None:
            continue
        vcs = read_json(folder / ".cargo_vcs_info.json") if (folder / ".cargo_vcs_info.json").exists() else {}
        commit = vcs.get("git", {}).get("sha1")
        package_metadata = {"authors": package.get("authors"), "source": package["source"], "license_file": package.get("license_file")}
        if vendor:
            package_metadata.update({"vendored_source": vendor["path"], "upstream_provenance": vendor["upstream"],
                                     "local_change_notice": "CCP-PATCH.md"})
            vendor_hashes.update(vendor["source_hashes"])
        sources.append({"ecosystem": "cargo", "name": package["name"], "version": package["version"],
            "license": package.get("license"), "repository": github(package.get("repository")) or github(package.get("homepage")) or vcs_repositories.get(commit),
            "commit": commit, "prefix": vcs.get("path_in_vcs", ""), "folder": folder,
            "package_metadata": package_metadata, "vendored": bool(vendor)})
    assets = read_json(root / "app/ColorCorrect.App/obj/project.assets.json")
    package_roots = [Path(p) for p in assets["packageFolders"]]
    for identity, description in read_json(published / "ColorCorrect.App.deps.json")["libraries"].items():
        if description["type"] not in ("package", "runtimepack"):
            continue
        name, version = identity.removeprefix("runtimepack.").split("/", 1)
        folder = next((p / name.lower() / version.lower() for p in package_roots if (p / name.lower() / version.lower()).is_dir()), None)
        if folder is None:
            raise ValueError("Restored NuGet package is missing: " + identity)
        nuspec = next(folder.glob("*.nuspec"))
        metadata_xml = next(node for node in ET.parse(nuspec).getroot() if node.tag.split("}")[-1] == "metadata")
        values = {node.tag.split("}")[-1]: node for node in metadata_xml}
        repo = values.get("repository")
        sources.append({"ecosystem": "nuget", "name": name, "version": version,
            "license": values["license"].text if "license" in values else None,
            "repository": github(repo.attrib.get("url")) if repo is not None else None,
            "commit": repo.attrib.get("commit") if repo is not None else None,
            "prefix": "", "folder": folder,
            "package_metadata": {key: values[key].text for key in ("authors", "copyright", "licenseUrl") if key in values}})
    if any(source["name"] == "Avalonia.Fonts.Inter" for source in sources):
        # The exact upstream revision is embedded in Avalonia's Inter-Regular.ttf
        # name table: Version 3.019;git-0a5106e0b. Font terms differ from code terms.
        sources.append({"ecosystem": "font", "name": "Inter", "version": "3.019", "license": "OFL-1.1",
            "repository": "rsms/inter", "commit": "0a5106e0bde18df09374066bf3a7998e3546307d", "prefix": "",
            "folder": root / "installer/upstream-font-not-checked-out",
            "package_metadata": {"copyright": "Copyright 2020 The Inter Project Authors (https://github.com/rsms/inter)",
                "provenance": "Inter-Regular.ttf name table in Avalonia eed76fc67ab9dd2b16e75667c23ee2335075dee5"}})

    def downloaded(url: str) -> bytes:
        location = cache / hashlib.sha256(url.encode()).hexdigest()
        if location.exists():
            return location.read_bytes()
        if not fetch:
            raise ValueError("Upstream notice is not cached; run collector with --fetch-upstream: " + url)
        request = urllib.request.Request(url, headers={"User-Agent": "ColorCorrect-notice-collector/1"})
        with urllib.request.urlopen(request, timeout=45) as response:
            data = response.read(8 * 1024 * 1024)
        location.write_bytes(data)
        return data

    repo_trees = {}
    wanted_repos = set()
    for source in sources:
        source["local_files"] = sorted(p for p in source["folder"].rglob("*") if p.is_file() and LEGAL.match(p.name))
        if source.get("vendored"):
            source["local_files"].append(source["folder"] / "CCP-PATCH.md")
        # Inter's font license is separate from Avalonia's code license.
        if (not source["local_files"] or source["name"] == "Avalonia.Fonts.Inter") and source["repository"] and re.fullmatch(r"[0-9a-fA-F]{40}", source["commit"] or ""):
            wanted_repos.add((source["repository"], source["commit"]))

    def get_tree(key):
        repo, commit = key
        url = f"https://api.github.com/repos/{repo}/git/trees/{commit}?recursive=1"
        tree = json.loads(downloaded(url))
        if tree.get("truncated"):
            raise ValueError("Upstream repository tree is truncated: " + repo)
        return key, [item["path"] for item in tree["tree"] if item["type"] == "blob" and LEGAL.match(Path(item["path"]).name)]
    with ThreadPoolExecutor(max_workers=6) as pool:
        for key, files in pool.map(get_tree, sorted(wanted_repos)):
            repo_trees[key] = files

    for source in sources:
        files = repo_trees.get((source["repository"], source["commit"]), [])
        prefix = source["prefix"].strip("/")
        source["upstream_files"] = [name for name in files if "/" not in name or name.split("/")[0].lower() in ("license", "licenses")
            or (prefix and name.startswith(prefix + "/")) or (source["name"] == "Avalonia.Fonts.Inter" and "inter" in name.lower())]
    urls = sorted({f"https://raw.githubusercontent.com/{s['repository']}/{s['commit']}/{name}" for s in sources for name in s["upstream_files"]})
    with ThreadPoolExecutor(max_workers=8) as pool:
        fetched = dict(zip(urls, pool.map(downloaded, urls)))
    report = {"schema": 1, "rid": rid, "scope": "Published NuGet runtime assets and active normal/build Rust dependency graph; developer-only and inactive optional backends excluded",
        "source_hashes": {name: digest(root / name) for name in ("engine/Cargo.lock", "app/ColorCorrect.App/ColorCorrect.App.csproj", "global.json", "rust-toolchain.toml")},
        "published_deps_sha256": digest(published / "ColorCorrect.App.deps.json"), "components": [], "unresolved": [], "sha256": {}}
    if vendor_hashes:
        report["source_hashes"].update(vendor_hashes)
        report["source_hashes"]["engine/Cargo.toml"] = digest(root / "engine/Cargo.toml")
    (output / "texts").mkdir()
    for source in sorted(sources, key=lambda s: (s["ecosystem"], s["name"], s["version"])):
        component = {k: source[k] for k in ("ecosystem", "name", "version", "license", "repository", "commit", "package_metadata")}
        component["notices"] = []
        local_origin = "vendored source" if source.get("vendored") else "restored package"
        documents = [(p.relative_to(source["folder"]).as_posix(), p.read_bytes(), local_origin) for p in source["local_files"]]
        if source.get("vendored"):
            component["corresponding_source"] = f"https://github.com/{source['repository']}/tree/{source['commit']}"
        documents += [(name, fetched[url], url) for name in source["upstream_files"] for url in [f"https://raw.githubusercontent.com/{source['repository']}/{source['commit']}/{name}"]]
        standard = STANDARD_TEXT.get((source["name"], source["version"]))
        if not documents and standard:
            url = f"https://raw.githubusercontent.com/spdx/license-list-data/{SPDX_COMMIT}/text/{standard}.txt"
            documents.append((standard + ".txt", downloaded(url), url))
            component["selected_license"] = standard
            component["notice_origin"] = "Exact package and pinned upstream contain no license file; declared license text supplied from pinned SPDX, original package authors retained"
            component["corresponding_source"] = f"https://github.com/{source['repository']}/tree/{source['commit']}"
        for name, content, provenance in documents:
            sha = hashlib.sha256(content).hexdigest()
            relative = "texts/" + sha + ".txt"
            (output / relative).write_bytes(content)
            report["sha256"][relative] = sha
            component["notices"].append({"original_name": name, "path": relative, "sha256": sha, "provenance": provenance})
        if not documents:
            report["unresolved"].append(f"{source['ecosystem']}:{source['name']}@{source['version']}: no license text in package or pinned upstream")
        report["components"].append(component)
    readme = ["# Collected dependency notices", "", report["scope"], "",
              "Each component retains its declared license, authors, upstream revision and text provenance in manifest.json.",
              "Text files are deduplicated by SHA-256. Source links identify the unmodified upstream revision; they do not replace applicable source-distribution obligations.", ""]
    for component in report["components"]:
        readme.append(f"- **{component['name']} {component['version']}** ({component.get('selected_license') or component['license'] or 'see included notice'}): " +
            ", ".join(f"[{notice['original_name']}]({notice['path']})" for notice in component["notices"]))
    (output / "README.md").write_text("\n".join(readme) + "\n", encoding="utf-8")
    report["sha256"]["README.md"] = digest(output / "README.md")
    (output / "manifest.json").write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")
    if report["unresolved"]:
        raise ValueError("Unresolved dependency licenses: " + "; ".join(report["unresolved"]))
    validate_notices(output, rid, root, published)
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--rid", required=True, choices=TARGETS)
    parser.add_argument("--app-publish", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cache", type=Path, default=Path(__file__).resolve().parents[1] / "_implementation/notice-cache")
    parser.add_argument("--cargo-metadata", type=Path)
    parser.add_argument("--fetch-upstream", action="store_true")
    args = parser.parse_args()
    report = collect(args.root.resolve(), args.rid, args.app_publish.resolve(), args.output.resolve(), args.cache.resolve(), args.fetch_upstream, args.cargo_metadata)
    print(json.dumps({"rid": args.rid, "components": len(report["components"]), "texts": len(report["sha256"]), "unresolved": report["unresolved"]}))
