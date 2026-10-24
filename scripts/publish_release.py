"""Publish verified Windows/Linux candidates from this version-tag workflow run.

Uses only the job's scoped GitHub token. Never builds, retags, deletes, replaces
assets, publishes macOS binaries, or marks a preview as the latest stable release.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import urllib.error
import urllib.parse
import urllib.request
import zipfile

from package_support import digest, native_version

RIDS = ("win-x64", "win-arm64", "linux-x64", "linux-arm64")
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
    if (run.get("head_sha") != sha or run.get("event") != "push"
            or run.get("path") != ".github/workflows/native-builds.yml"
            or run.get("repository", {}).get("full_name", "").lower() != github.prefix.removeprefix("/repos/").lower()):
        raise ValueError("The release candidates did not originate from this trusted workflow run")
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


def distribution_names(version: str, rid: str) -> set[str]:
    stem = f"ColorCorrect-Studio-{version}-{rid}"
    if rid.startswith("win-"):
        return {stem + ".zip", stem + "-Setup.exe"}
    architecture = "amd64" if rid == "linux-x64" else "arm64"
    return {stem + ".tar.gz", f"colorcorrect-studio_{version}_{architecture}.deb"}


def stage_candidate(archive: Path, output: Path, version: str, rid: str, sha: str) -> list[Path]:
    expected = distribution_names(version, rid)
    with zipfile.ZipFile(archive) as bundle:
        entries = bundle.infolist()
        names = [entry.filename for entry in entries]
        if len(names) != len(set(names)) or set(names) != expected | {"SHA256SUMS", "package-manifest.json"}:
            raise ValueError("Candidate contains missing, duplicate, or unapproved release assets")
        if any(entry.is_dir() or (entry.external_attr >> 16) & 0o170000 == 0o120000 for entry in entries):
            raise ValueError("Candidate release assets must be ordinary files")
        manifest = json.loads(bundle.read("package-manifest.json"))
        if (manifest.get("rid") != rid or manifest.get("version") != version
                or manifest.get("source", {}).get("git_commit") != sha
                or manifest.get("source", {}).get("working_tree_dirty") is not False
                or manifest.get("media_scope") != "still images"
                or set(manifest.get("worker_features_declared", [])) != PRODUCTION_FEATURES):
            raise ValueError("Candidate manifest has incompatible source, version, architecture, or features")
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


def stage_run(github: GitHub, run_id: int, sha: str, version: str, output: Path) -> list[Path]:
    output.mkdir(parents=True, exist_ok=False)
    downloads = output / "artifact-downloads"
    downloads.mkdir()
    artifacts = list(github.pages(f"/actions/runs/{run_id}/artifacts", "artifacts"))
    files = []
    for rid in RIDS:
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
        files += stage_candidate(archive, output, version, rid, sha)
    combined = output / "SHA256SUMS"
    combined.write_text("".join(f"{digest(file)}  {file.name}\n" for file in sorted(files)), encoding="ascii", newline="\n")
    return files + [combined]


def verify_asset(asset: dict, file: Path):
    if (asset.get("name") != file.name or asset.get("size") != file.stat().st_size
            or asset.get("state") != "uploaded" or asset.get("digest") != "sha256:" + digest(file)):
        raise ValueError("Existing or uploaded release asset is not identical: " + file.name)


def publish(github: GitHub, run_id: int, sha: str, tag: str, files: list[Path]) -> dict:
    verify_source(github, run_id, sha, tag)
    marker = f"<!-- colorcorrect-source:{sha} -->"
    matches = [release for release in github.pages("/releases") if release["tag_name"] == tag]
    if len(matches) > 1:
        raise ValueError("Ambiguous existing release")
    release = matches[0] if matches else None
    if release and (marker not in release.get("body", "") or release.get("target_commitish") != sha
                    or release.get("prerelease") is not True):
        raise ValueError("Refusing to change a release not created by this verified preview workflow")
    if release is None:
        release = github.request(github.prefix + "/releases", method="POST", data={
            "tag_name": tag, "target_commitish": sha, "name": f"ColorCorrect Studio {tag[1:]} (preview)",
            "draft": True, "prerelease": True, "make_latest": "false",
            "body": marker + "\n\nWindows and Ubuntu 24.04 downloads for x64 and ARM64. "
                    "Installers/packages and portable archives include the Python API wheel.\n\n"
                    "This preview passed native build, installation, component and CPU SDK smoke checks. "
                    "Native GPU performance and interactive UI acceptance remain separate release gates. "
                    "Apple-silicon macOS is source-only; no macOS application binaries are distributed.\n\n"
                    "Each architecture has a package manifest and checksums; SHA256SUMS covers all other release assets."})
    release_id = release["id"]
    existing = {asset["name"]: asset for asset in github.pages(f"/releases/{release_id}/assets")}
    wanted = {file.name for file in files}
    if set(existing) - wanted:
        raise ValueError("Existing release contains unexpected assets; nothing was replaced")
    if not release["draft"] and set(existing) != wanted:
        raise ValueError("Published releases are immutable; missing assets cannot be added")
    for file in files:
        asset = existing.get(file.name)
        if asset is None:
            asset = github.request(github.prefix + f"/releases/{release_id}/assets?name=" + urllib.parse.quote(file.name, safe=""),
                                   method="POST", upload=file)
        verify_asset(asset, file)
    verify_source(github, run_id, sha, tag)  # Recheck the live tag immediately before publication.
    if release["draft"]:
        release = github.request(github.prefix + f"/releases/{release_id}", method="PATCH",
                                 data={"draft": False, "prerelease": True, "make_latest": "false"})
    if release.get("draft") is not False or release.get("prerelease") is not True:
        raise ValueError("GitHub did not confirm the requested preview publication")
    return release


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--publish", action="store_true", help="explicitly publish after all local and remote verification")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    version = native_version(root)
    repository, run_id, sha, tag = validate_context(version, os.environ)
    github = GitHub(repository, os.environ.get("GH_TOKEN", ""))
    verify_source(github, run_id, sha, tag)
    files = stage_run(github, run_id, sha, version, args.output.resolve())
    print(json.dumps({"tag": tag, "source": sha, "assets": [file.name for file in files], "prerelease": True}), flush=True)
    if args.publish:
        release = publish(github, run_id, sha, tag, files)
        print(json.dumps({"release": release["html_url"], "prerelease": release["prerelease"]}), flush=True)


if __name__ == "__main__":
    main()
