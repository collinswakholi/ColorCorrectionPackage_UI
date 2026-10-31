"""Publish allowlisted candidates from a verified version-tag workflow run.

Defaults to Windows/Linux previews. Stable/latest publication and Apple-silicon
macOS application assets each require an explicit option. Never retags, deletes,
replaces assets, or modifies an already published release.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile

from package_support import digest, native_version

RIDS = ("win-x64", "win-arm64", "linux-x64", "linux-arm64")
MAC_RID = "osx-arm64"
PRODUCTION_FEATURES = {"ort-plane", "wgpu-lut", "rust-inference"}
API = "https://api.github.com"


class TokenSafeRedirect(urllib.request.HTTPRedirectHandler):
    """Artifact storage redirects must never receive the GitHub bearer token."""
    def redirect_request(self, request, fp, code, message, headers, newurl):
        if urllib.parse.urlsplit(newurl).scheme != "https":
            raise ValueError("Refusing a non-HTTPS GitHub download redirect")
        redirected = super().redirect_request(request, fp, code, message, headers, newurl)
        if urllib.parse.urlsplit(request.full_url).netloc != urllib.parse.urlsplit(newurl).netloc:
            redirected.remove_header("Authorization")
        return redirected


class GitHub:
    def __init__(self, repository: str, token: str):
        if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository) or not token:
            raise ValueError("A valid repository and scoped GitHub token are required")
        self.prefix = f"/repos/{repository}"
        self.opener = urllib.request.build_opener(TokenSafeRedirect())
        self.headers = {"Authorization": "Bearer " + token, "Accept": "application/vnd.github+json",
                        "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "ColorCorrect-release/1"}

    def request(self, path: str, *, method="GET", data=None, upload: Path | None = None):
        if not path.startswith(self.prefix + "/"):
            raise ValueError("GitHub request escaped the selected repository")
        headers = dict(self.headers)
        if upload is not None:
            url = "https://uploads.github.com" + path
            headers["Content-Type"] = "application/octet-stream"
            body = upload.read_bytes()
        else:
            url = API + path
            headers["Content-Type"] = "application/json"
            body = None if data is None else json.dumps(data).encode("utf-8")
        with self.opener.open(urllib.request.Request(url, body, headers, method=method), timeout=180) as response:
            return json.load(response)

    def download(self, artifact_id: int, destination: Path):
        request = urllib.request.Request(API + self.prefix + f"/actions/artifacts/{artifact_id}/zip", headers=self.headers)
        with self.opener.open(request, timeout=180) as response, destination.open("xb") as output:
            shutil.copyfileobj(response, output)

    def pages(self, resource: str, key: str | None = None):
        for page in range(1, 101):
            value = self.request(self.prefix + resource + f"?per_page=100&page={page}")
            rows = value[key] if key else value
            yield from rows
            if len(rows) < 100:
                return
        raise ValueError("GitHub pagination exceeded the reviewed release limit")


def validate_context(version: str, environment: dict) -> tuple[str, int, str, str]:
    repository, sha, tag = (environment.get(name, "") for name in ("GITHUB_REPOSITORY", "GITHUB_SHA", "GITHUB_REF_NAME"))
    if environment.get("GITHUB_EVENT_NAME") != "push" or environment.get("GITHUB_REF") != "refs/tags/" + tag:
        raise ValueError("Only an explicit version-tag push may publish")
    if tag != "v" + version or not re.fullmatch(r"v\d+\.\d+\.\d+", tag):
        raise ValueError("The version tag must match the native application version")
    if not re.fullmatch(r"[0-9a-f]{40}", sha) or not environment.get("GITHUB_RUN_ID", "").isdigit():
        raise ValueError("Missing immutable workflow-run identity")
    return repository, int(environment["GITHUB_RUN_ID"]), sha, tag


def verify_source(github: GitHub, run_id: int, sha: str, tag: str):
    run = github.request(github.prefix + f"/actions/runs/{run_id}")
    if (run.get("head_sha") != sha or run.get("head_branch") != tag or run.get("event") != "push"
            or run.get("path") != ".github/workflows/native-builds.yml"
            or run.get("repository", {}).get("full_name", "").lower() != github.prefix.removeprefix("/repos/").lower()):
        raise ValueError("The release candidates did not originate from this trusted workflow run")
    validation = [job for job in github.pages(f"/actions/runs/{run_id}/jobs", "jobs")
                  if job.get("name") == "Validation result"]
    if len(validation) != 1 or validation[0].get("status") != "completed" or validation[0].get("conclusion") != "success":
        raise ValueError("The current workflow attempt must complete its aggregate validation before publication")
    reference = github.request(github.prefix + "/git/ref/tags/" + tag)["object"]
    for _ in range(5):
        if reference["type"] == "commit":
            break
        if reference["type"] != "tag":
            raise ValueError("Unsupported version-tag object")
        reference = github.request(github.prefix + "/git/tags/" + reference["sha"])["object"]
    if reference["type"] != "commit" or reference["sha"] != sha:
        raise ValueError("The live version tag changed after this workflow started")
    comparison = github.request(github.prefix + f"/compare/{sha}...main")
    if comparison.get("behind_by") != 0 or comparison.get("merge_base_commit", {}).get("sha") != sha:
        raise ValueError("The tagged source must belong to main")


def release_rids(include_macos_arm64: bool = False) -> tuple[str, ...]:
    return RIDS + ((MAC_RID,) if include_macos_arm64 else ())


def distribution_names(version: str, rid: str) -> set[str]:
    if rid not in release_rids(True):
        raise ValueError("Unsupported release architecture: " + rid)
    stem = f"ColorCorrect-Studio-{version}-{rid}"
    if rid == MAC_RID:
        return {stem + ".zip", stem + ".tar.gz"}
    if rid.startswith("win-"):
        return {stem + ".zip", stem + "-Setup.exe"}
    architecture = "amd64" if rid == "linux-x64" else "arm64"
    return {stem + ".tar.gz", f"colorcorrect-studio_{version}_{architecture}.deb"}


def validate_manifest(manifest: dict, version: str, rid: str, sha: str, include_macos_arm64: bool):
    if (not isinstance(manifest, dict) or not isinstance(manifest.get("source"), dict)
            or not isinstance(manifest.get("worker_features_declared"), list)
            or not all(isinstance(feature, str) for feature in manifest["worker_features_declared"])):
        raise ValueError("Candidate manifest must contain structured source and feature metadata")
    if (manifest.get("rid") != rid or manifest.get("version") != version
            or manifest.get("source", {}).get("git_commit") != sha
            or manifest.get("source", {}).get("working_tree_dirty") is not False
            or manifest.get("media_scope") != "still images"
            or len(manifest["worker_features_declared"]) != len(PRODUCTION_FEATURES)
            or set(manifest["worker_features_declared"]) != PRODUCTION_FEATURES):
        raise ValueError("Candidate manifest has incompatible source, version, architecture, or features")
    if rid == MAC_RID and (not include_macos_arm64
            or manifest.get("distribution_policy") != "application"
            or manifest.get("architecture") != "arm64"
            or manifest.get("minimum_os") != "macOS 14.0"
            or not isinstance(manifest.get("signing"), dict)
            or set(manifest["signing"]) != {"kind", "notarized"}
            or manifest["signing"].get("kind") != "ad-hoc"
            or manifest["signing"].get("notarized") is not False):
        raise ValueError("Apple-silicon application distribution requires explicit opt-in and its binary manifest")


def release_asset_names(version: str, include_macos_arm64: bool = False) -> set[str]:
    names = {"SHA256SUMS"}
    for rid in release_rids(include_macos_arm64):
        names |= distribution_names(version, rid) | {f"SHA256SUMS-{rid}.txt", f"package-manifest-{rid}.json"}
    return names


def stage_candidate(archive: Path, output: Path, version: str, rid: str, sha: str, *, include_macos_arm64=False) -> list[Path]:
    if rid == MAC_RID and not include_macos_arm64:
        raise ValueError("macOS binary staging requires explicit opt-in")
    expected = distribution_names(version, rid)
    with zipfile.ZipFile(archive) as bundle:
        entries = bundle.infolist()
        names = [entry.filename for entry in entries]
        if len(names) != len(set(names)) or set(names) != expected | {"SHA256SUMS", "package-manifest.json"}:
            raise ValueError("Candidate contains missing, duplicate, or unapproved release assets")
        if any(entry.is_dir() or (entry.external_attr >> 16) & 0o170000 == 0o120000 for entry in entries):
            raise ValueError("Candidate release assets must be ordinary files")
        manifest = json.loads(bundle.read("package-manifest.json"))
        validate_manifest(manifest, version, rid, sha, include_macos_arm64)
        checksums = {}
        for line in bundle.read("SHA256SUMS").decode("ascii").splitlines():
            match = re.fullmatch(r"([0-9a-f]{64})  ([A-Za-z0-9_.-]+)", line)
            if match is None or match[2] in checksums:
                raise ValueError("Malformed or duplicate candidate checksum")
            checksums[match[2]] = match[1]
        if set(checksums) != expected:
            raise ValueError("Candidate checksum inventory does not match its distributions")
        files = []
        for name in sorted(expected):
            destination = output / name
            with bundle.open(name) as source, destination.open("xb") as target:
                shutil.copyfileobj(source, target)
            if digest(destination) != checksums[name]:
                raise ValueError("Candidate distribution checksum mismatch: " + name)
            files.append(destination)
        for original, renamed in (("SHA256SUMS", f"SHA256SUMS-{rid}.txt"),
                                  ("package-manifest.json", f"package-manifest-{rid}.json")):
            destination = output / renamed
            with destination.open("xb") as stream:
                stream.write(bundle.read(original))
            files.append(destination)
        return files


def stage_run(github: GitHub, run_id: int, sha: str, version: str, output: Path, *, include_macos_arm64=False) -> list[Path]:
    output.mkdir(parents=True, exist_ok=False)
    downloads = output / "artifact-downloads"
    downloads.mkdir()
    artifacts = list(github.pages(f"/actions/runs/{run_id}/artifacts", "artifacts"))
    allowed_candidates = {f"candidate-{rid}-{sha}" for rid in release_rids(include_macos_arm64)}
    if any(item["name"].startswith("candidate-") and item["name"].endswith("-" + sha)
           and item["name"] not in allowed_candidates for item in artifacts):
        raise ValueError("Workflow contains a candidate outside the selected distribution policy")
    files = []
    for rid in release_rids(include_macos_arm64):
        matches = [item for item in artifacts if item["name"] == f"candidate-{rid}-{sha}" and not item["expired"]]
        if len(matches) != 1:
            raise ValueError("Exactly one candidate artifact is required for " + rid)
        artifact_digest = matches[0].get("digest", "")
        if not re.fullmatch(r"sha256:[0-9a-f]{64}", artifact_digest):
            raise ValueError("Candidate artifact lacks its GitHub archive checksum: " + rid)
        archive = downloads / (rid + ".zip")
        github.download(matches[0]["id"], archive)
        if "sha256:" + digest(archive) != artifact_digest:
            raise ValueError("Downloaded GitHub artifact checksum mismatch: " + rid)
        files += stage_candidate(archive, output, version, rid, sha, include_macos_arm64=include_macos_arm64)
    combined = output / "SHA256SUMS"
    combined.write_text("".join(f"{digest(file)}  {file.name}\n" for file in sorted(files)), encoding="ascii", newline="\n")
    return files + [combined]


def verify_asset(asset: dict, file: Path):
    if (asset.get("name") != file.name or asset.get("size") != file.stat().st_size
            or asset.get("state") != "uploaded" or asset.get("digest") != "sha256:" + digest(file)):
        raise ValueError("Existing or uploaded release asset is not identical: " + file.name)


def policy_marker(stable: bool, include_macos_arm64: bool) -> str:
    policy = {"schema": 1, "mode": "stable" if stable else "preview", "rids": list(release_rids(include_macos_arm64))}
    return "<!-- colorcorrect-release-policy:" + json.dumps(policy, sort_keys=True, separators=(",", ":")) + " -->"


def verify_local_assets(files: list[Path], version: str, sha: str, include_macos_arm64: bool):
    names = [file.name for file in files]
    if len(set(names)) != len(names) or set(names) != release_asset_names(version, include_macos_arm64):
        raise ValueError("Release must contain the complete allowlisted asset inventory")
    if any(not file.is_file() or file.is_symlink() for file in files):
        raise ValueError("Release assets must be ordinary files")
    by_name = {file.name: file for file in files}
    combined = {}
    for line in by_name["SHA256SUMS"].read_text(encoding="ascii").splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  ([A-Za-z0-9_.-]+)", line)
        if match is None or match[2] in combined:
            raise ValueError("Malformed combined release checksum inventory")
        combined[match[2]] = match[1]
    if combined != {file.name: digest(file) for file in files if file.name != "SHA256SUMS"}:
        raise ValueError("Combined release checksums do not match the complete asset inventory")
    for rid in release_rids(include_macos_arm64):
        validate_manifest(json.loads(by_name[f"package-manifest-{rid}.json"].read_text(encoding="utf-8")),
                          version, rid, sha, include_macos_arm64)
        checksums = {}
        for line in by_name[f"SHA256SUMS-{rid}.txt"].read_text(encoding="ascii").splitlines():
            match = re.fullmatch(r"([0-9a-f]{64})  ([A-Za-z0-9_.-]+)", line)
            if match is None or match[2] in checksums:
                raise ValueError("Malformed architecture checksum inventory: " + rid)
            checksums[match[2]] = match[1]
        if checksums != {name: digest(by_name[name]) for name in distribution_names(version, rid)}:
            raise ValueError("Architecture checksums do not match distributions: " + rid)


def verify_release_identity(release: dict, sha: str, tag: str, stable: bool, include_macos_arm64: bool):
    if not isinstance(release, dict) or not isinstance(release.get("body"), str):
        raise ValueError("Malformed existing release identity")
    body = release.get("body") or ""
    sources = re.findall(r"<!-- colorcorrect-source:([0-9a-f]{40}) -->", body)
    policies = re.findall(r"<!-- colorcorrect-release-policy:.*? -->", body)
    legacy_preview = not stable and not include_macos_arm64 and not policies
    if (sources != [sha] or release.get("tag_name") != tag or release.get("target_commitish") != sha
            or release.get("prerelease") is not (not stable)
            or not isinstance(release.get("draft"), bool)
            or type(release.get("id")) is not int or release["id"] <= 0
            or not (legacy_preview or policies == [policy_marker(stable, include_macos_arm64)])):
        raise ValueError("Refusing to change a release with different source or distribution policy")


def verified_remote_assets(github: GitHub, release_id: int, files: list[Path], *, complete=False) -> dict:
    rows = list(github.pages(f"/releases/{release_id}/assets"))
    if any(not isinstance(asset, dict) or not isinstance(asset.get("name"), str) for asset in rows):
        raise ValueError("Malformed release asset metadata")
    names = [asset.get("name") for asset in rows]
    wanted = {file.name for file in files}
    if len(set(names)) != len(names) or set(names) - wanted or (complete and set(names) != wanted):
        raise ValueError("Release has duplicate, missing, or unexpected assets; nothing was replaced")
    existing = dict(zip(names, rows))
    for file in files:
        if file.name in existing:
            verify_asset(existing[file.name], file)
    return existing


def publish(github: GitHub, run_id: int, sha: str, tag: str, files: list[Path], *, stable=False, include_macos_arm64=False) -> dict:
    if not re.fullmatch(r"v\d+\.\d+\.\d+", tag):
        raise ValueError("Publication requires an exact semantic version tag")
    verify_local_assets(files, tag[1:], sha, include_macos_arm64)
    verify_source(github, run_id, sha, tag)
    marker = f"<!-- colorcorrect-source:{sha} -->"
    matches = [release for release in github.pages("/releases") if release["tag_name"] == tag]
    if len(matches) > 1:
        raise ValueError("Ambiguous existing release")
    release = matches[0] if matches else None
    if release:
        verify_release_identity(release, sha, tag, stable, include_macos_arm64)
    if release is None:
        release = github.request(github.prefix + "/releases", method="POST", data={
            "tag_name": tag, "target_commitish": sha, "name": f"ColorCorrect Studio {tag[1:]}" + ("" if stable else " (preview)"),
            "draft": True, "prerelease": not stable, "make_latest": "false",
            "body": marker + "\n" + policy_marker(stable, include_macos_arm64) + "\n\nWindows and Ubuntu 24.04 downloads for x64 and ARM64. "
                    "Installers/packages and portable archives include the Python API wheel.\n\n"
                    "Native build, installation, component and CPU/Auto SDK workflows are checked per architecture. "
                    "Physical GPU and interactive UI coverage remain platform-specific. "
                    + ("Apple-silicon macOS 14+ application archives are included; the app is ad-hoc signed, not notarized. "
                       if include_macos_arm64 else "Apple-silicon macOS is source-only; no macOS application binaries are distributed. ")
                    + "Windows/Linux builds are unsigned. No Intel Mac, Torch or video processing.\n\n"
                    "Each architecture has a package manifest and checksums; SHA256SUMS covers all other release assets.\n\n"
                    f"[Changes and upgrade details](https://github.com/{github.prefix.removeprefix('/repos/')}/blob/{tag}/CHANGELOG.md)."})
        verify_release_identity(release, sha, tag, stable, include_macos_arm64)
    release_id = release["id"]
    existing = verified_remote_assets(github, release_id, files, complete=not release["draft"])
    for file in files:
        asset = existing.get(file.name)
        if asset is None:
            asset = github.request(github.prefix + f"/releases/{release_id}/assets?name=" + urllib.parse.quote(file.name, safe=""),
                                   method="POST", upload=file)
        verify_asset(asset, file)
    verified_remote_assets(github, release_id, files, complete=True)
    verify_source(github, run_id, sha, tag)  # Recheck the live tag immediately before publication.
    if release["draft"]:
        release = github.request(github.prefix + f"/releases/{release_id}", method="PATCH",
                                 data={"draft": False, "prerelease": not stable, "make_latest": "true" if stable else "false"})
    if release.get("draft") is not False or release.get("prerelease") is not (not stable):
        raise ValueError("GitHub did not confirm the requested publication state")
    verify_release_identity(release, sha, tag, stable, include_macos_arm64)
    if release["id"] != release_id:
        raise ValueError("GitHub returned a different release after publication")
    if stable:
        for attempt in range(3):
            latest = github.request(github.prefix + "/releases/latest")
            if latest.get("id") == release_id and latest.get("tag_name") == tag:
                break
            if attempt == 2:
                raise ValueError("GitHub did not confirm this full release as latest")
            time.sleep(1)
    return release


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--publish", action="store_true", help="explicitly publish after all local and remote verification")
    parser.add_argument("--stable", action="store_true", help="publish a full release and explicitly mark it latest")
    parser.add_argument("--include-macos-arm64", action="store_true", help="require and include verified Apple-silicon application archives")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    version = native_version(root)
    repository, run_id, sha, tag = validate_context(version, os.environ)
    github = GitHub(repository, os.environ.get("GH_TOKEN", ""))
    verify_source(github, run_id, sha, tag)
    files = stage_run(github, run_id, sha, version, args.output.resolve(), include_macos_arm64=args.include_macos_arm64)
    print(json.dumps({"tag": tag, "source": sha, "assets": [file.name for file in files], "prerelease": not args.stable,
                      "macos_arm64": args.include_macos_arm64}), flush=True)
    if args.publish:
        release = publish(github, run_id, sha, tag, files, stable=args.stable, include_macos_arm64=args.include_macos_arm64)
        print(json.dumps({"release": release["html_url"], "prerelease": release["prerelease"]}), flush=True)


if __name__ == "__main__":
    main()
