"""Adversarial release tests; all GitHub writes and downloads are fake."""
from pathlib import Path
import hashlib
import json
import shutil
import sys
import tempfile
import unittest
import urllib.request
import zipfile
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import publish_release as release

SHA = "a" * 40
TAG = "v0.2.0"


def candidate(path, rid="win-x64", *, manifest_change=None, corrupt=False, extra=None):
    payload = {name: ("contents:" + name).encode() for name in release.distribution_names("0.2.0", rid)}
    manifest = {"rid": rid, "version": "0.2.0", "source": {"git_commit": SHA, "working_tree_dirty": False},
                "media_scope": "still images", "worker_features_declared": ["ort-plane", "wgpu-lut", "rust-inference"]}
    if manifest_change:
        manifest.update(manifest_change)
    checksums = "".join(hashlib.sha256(data).hexdigest() + "  " + name + "\n" for name, data in sorted(payload.items()))
    with zipfile.ZipFile(path, "w") as archive:
        for name, data in payload.items():
            archive.writestr(name, data + (b"bad" if corrupt else b""))
        archive.writestr("SHA256SUMS", checksums)
        archive.writestr("package-manifest.json", json.dumps(manifest))
        if extra:
            archive.writestr(extra, b"not approved")
    return payload


class FakeGitHub:
    prefix = "/repos/owner/repo"

    def __init__(self, previous=None, assets=()):
        self.previous = previous
        self.assets = list(assets)
        self.writes = []

    def pages(self, resource, key=None):
        return iter(self.assets if resource.endswith("/assets") else ([] if self.previous is None else [self.previous]))

    def request(self, path, *, method="GET", data=None, upload=None):
        self.writes.append((path, method, data, None if upload is None else upload.name))
        if upload:
            asset = {"name": upload.name, "size": upload.stat().st_size, "state": "uploaded", "digest": "sha256:" + release.digest(upload)}
            self.assets.append(asset)
            return asset
        if method == "POST":
            self.previous = dict(data, id=9, html_url="https://github.com/owner/repo/releases/tag/" + TAG)
        elif method == "PATCH":
            self.previous.update(data)
        return self.previous


class PublishReleaseTests(unittest.TestCase):
    @staticmethod
    def artifact_api(root):
        class ArtifactAPI:
            def __init__(self):
                self.artifacts = []
                self.archives = {}
                for number, rid in enumerate(release.RIDS, 1):
                    archive = root / (rid + ".zip")
                    candidate(archive, rid)
                    self.archives[number] = archive
                    self.artifacts.append({"id": number, "name": f"candidate-{rid}-{SHA}", "expired": False,
                                           "digest": "sha256:" + release.digest(archive)})
            def pages(self, resource, key):
                return iter(self.artifacts)
            def download(self, artifact_id, destination):
                shutil.copy2(self.archives[artifact_id], destination)
        return ArtifactAPI()

    def test_complete_run_stages_seventeen_assets_with_combined_checksums(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            api = self.artifact_api(root)
            files = release.stage_run(api, 123, SHA, "0.2.0", root / "out")
            self.assertEqual(len(files), 17)
            combined = {name: checksum for checksum, name in
                        (line.split("  ") for line in (root / "out/SHA256SUMS").read_text().splitlines())}
            self.assertEqual(combined, {file.name: release.digest(file) for file in files if file.name != "SHA256SUMS"})

    def test_github_archive_digest_is_required_and_verified(self):
        for value in (None, "sha256:" + "0" * 64):
            with self.subTest(digest=value), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                api = self.artifact_api(root)
                if value is None:
                    api.artifacts[0].pop("digest")
                else:
                    api.artifacts[0]["digest"] = value
                with self.assertRaisesRegex(ValueError, "checksum"):
                    release.stage_run(api, 123, SHA, "0.2.0", root / "out")

    def test_context_rejects_non_tag_runs_wrong_versions_and_shell_metacharacters(self):
        environment = {"GITHUB_REPOSITORY": "owner/repo", "GITHUB_SHA": SHA, "GITHUB_REF_NAME": TAG,
                       "GITHUB_REF": "refs/tags/" + TAG, "GITHUB_EVENT_NAME": "push", "GITHUB_RUN_ID": "123"}
        self.assertEqual(release.validate_context("0.2.0", environment), ("owner/repo", 123, SHA, TAG))
        for changes in ({"GITHUB_EVENT_NAME": "pull_request"}, {"GITHUB_REF": "refs/heads/main"},
                        {"GITHUB_REF_NAME": "v0.2.0$(id)"}, {"GITHUB_SHA": "main"}, {"GITHUB_RUN_ID": ""}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                release.validate_context("0.2.0", dict(environment, **changes))
        with self.assertRaises(ValueError):
            release.validate_context("0.2.1", environment)

    def test_cross_host_artifact_redirect_drops_authorization_and_requires_https(self):
        handler = release.TokenSafeRedirect()
        original = urllib.request.Request("https://api.github.com/artifact", headers={"Authorization": "Bearer secret"})
        redirected = handler.redirect_request(original, None, 302, "Found", {}, "https://storage.example/artifact.zip")
        self.assertIsNone(redirected.get_header("Authorization"))
        same_host = handler.redirect_request(original, None, 302, "Found", {}, "https://api.github.com/new")
        self.assertEqual(same_host.get_header("Authorization"), "Bearer secret")
        with self.assertRaises(ValueError):
            handler.redirect_request(original, None, 302, "Found", {}, "http://storage.example/artifact.zip")

    def test_source_validation_rejects_wrong_run_moved_tag_and_non_main_commit(self):
        class SourceAPI:
            prefix = "/repos/owner/repo"
            def __init__(self):
                self.values = {"/actions/runs/123": {"head_sha": SHA, "event": "push", "path": ".github/workflows/native-builds.yml",
                                                     "repository": {"full_name": "owner/repo"}},
                               "/git/ref/tags/" + TAG: {"object": {"type": "commit", "sha": SHA}},
                               "/compare/" + SHA + "...main": {"behind_by": 0, "merge_base_commit": {"sha": SHA}}}
            def request(self, path):
                return self.values[path.removeprefix(self.prefix)]
        api = SourceAPI()
        release.verify_source(api, 123, SHA, TAG)
        for endpoint, field, value in (("/actions/runs/123", "head_sha", "b" * 40),
                                       ("/git/ref/tags/" + TAG, "object", {"type": "commit", "sha": "b" * 40}),
                                       ("/compare/" + SHA + "...main", "behind_by", 1)):
            with self.subTest(endpoint=endpoint):
                broken = SourceAPI()
                broken.values[endpoint][field] = value
                with self.assertRaises(ValueError):
                    release.verify_source(broken, 123, SHA, TAG)

    def test_all_four_candidate_layouts_have_unique_named_metadata(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            names = []
            for rid in release.RIDS:
                archive, output = root / (rid + ".zip"), root / rid
                output.mkdir()
                expected = candidate(archive, rid)
                files = release.stage_candidate(archive, output, "0.2.0", rid, SHA)
                self.assertEqual({file.name for file in files}, set(expected) | {f"SHA256SUMS-{rid}.txt", f"package-manifest-{rid}.json"})
                names += [file.name for file in files]
            self.assertEqual(len(names), 16)
            self.assertEqual(len(set(names)), 16)
            self.assertFalse(any("osx" in name or name.endswith((".dmg", ".pkg", ".whl")) for name in names))

    def test_candidate_rejects_tampering_wrong_provenance_and_extra_mac_or_path_assets(self):
        cases = [{"corrupt": True}, {"manifest_change": {"version": "9.9.9"}},
                 {"manifest_change": {"source": {"git_commit": SHA, "working_tree_dirty": True}}},
                 {"manifest_change": {"source": {"git_commit": "b" * 40, "working_tree_dirty": False}}},
                 {"manifest_change": {"worker_features_declared": ["torch"]}},
                 {"manifest_change": {"worker_features_declared": []}},
                 {"manifest_change": {"worker_features_declared": ["ort-plane", "wgpu-lut", "rust-inference", "unknown"]}},
                 {"extra": "app.dmg"}, {"extra": "../escape.exe"}]
        for options in cases:
            with self.subTest(options=options), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                archive, output = root / "candidate.zip", root / "out"
                output.mkdir()
                candidate(archive, **options)
                with self.assertRaises(ValueError):
                    release.stage_candidate(archive, output, "0.2.0", "win-x64", SHA)

    @patch("publish_release.verify_source")
    def test_publication_uploads_verified_draft_before_publishing_never_latest(self, source):
        with tempfile.TemporaryDirectory() as temporary:
            file = Path(temporary) / "candidate.zip"
            file.write_bytes(b"complete distribution")
            api = FakeGitHub()
            result = release.publish(api, 123, SHA, TAG, [file])
            self.assertEqual([operation[1] for operation in api.writes], ["POST", "POST", "PATCH"])
            self.assertTrue(api.writes[0][2]["draft"])
            self.assertTrue(api.writes[0][2]["prerelease"])
            self.assertEqual(api.writes[0][2]["make_latest"], "false")
            self.assertEqual(api.writes[-1][2], {"draft": False, "prerelease": True, "make_latest": "false"})
            self.assertFalse(result["draft"])
            self.assertEqual(source.call_count, 2)

    @patch("publish_release.verify_source")
    def test_published_assets_are_never_replaced_or_added(self, source):
        with tempfile.TemporaryDirectory() as temporary:
            file = Path(temporary) / "candidate.zip"
            file.write_bytes(b"complete distribution")
            previous = {"id": 9, "tag_name": TAG, "body": f"<!-- colorcorrect-source:{SHA} -->", "target_commitish": SHA,
                        "draft": False, "prerelease": True}
            asset = {"name": file.name, "size": file.stat().st_size, "state": "uploaded", "digest": "sha256:" + release.digest(file)}
            api = FakeGitHub(previous, [asset])
            release.publish(api, 123, SHA, TAG, [file])
            self.assertEqual(api.writes, [])
            for assets in ([], [dict(asset, digest="sha256:" + "0" * 64)], [dict(asset, name="unexpected.dmg")]):
                api = FakeGitHub(previous, assets)
                with self.assertRaises(ValueError):
                    release.publish(api, 123, SHA, TAG, [file])
                self.assertEqual(api.writes, [])

    @patch("publish_release.verify_source")
    def test_resume_own_draft_reuses_only_identical_existing_assets(self, source):
        with tempfile.TemporaryDirectory() as temporary:
            files = [Path(temporary) / name for name in ("one.zip", "two.zip")]
            for file in files:
                file.write_bytes(file.name.encode())
            previous = {"id": 9, "tag_name": TAG, "body": f"<!-- colorcorrect-source:{SHA} -->", "target_commitish": SHA,
                        "draft": True, "prerelease": True}
            asset = {"name": files[0].name, "size": files[0].stat().st_size, "state": "uploaded", "digest": "sha256:" + release.digest(files[0])}
            api = FakeGitHub(previous, [asset])
            release.publish(api, 123, SHA, TAG, files)
            self.assertEqual([operation[3] for operation in api.writes if operation[1] == "POST"], ["two.zip"])
            self.assertEqual(api.writes[-1][1], "PATCH")


if __name__ == "__main__":
    unittest.main()
