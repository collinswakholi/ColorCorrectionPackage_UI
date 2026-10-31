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
    if rid == release.MAC_RID:
        manifest.update(distribution_policy="application", architecture="arm64", minimum_os="macOS 14.0",
                        signing={"kind": "ad-hoc", "notarized": False})
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
        if method != "GET":
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
    def artifact_api(root, include_macos_arm64=False):
        class ArtifactAPI:
            def __init__(self):
                self.artifacts = []
                self.archives = {}
                for number, rid in enumerate(release.release_rids(include_macos_arm64), 1):
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

    def staged_files(self, root, include_macos_arm64=False):
        return release.stage_run(self.artifact_api(root, include_macos_arm64), 123, SHA, "0.2.0", root / "out",
                                 include_macos_arm64=include_macos_arm64)

    @staticmethod
    def asset(file):
        return {"name": file.name, "size": file.stat().st_size, "state": "uploaded", "digest": "sha256:" + release.digest(file)}

    @staticmethod
    def previous(*, draft, stable=False, include_macos_arm64=False):
        return {"id": 9, "tag_name": TAG, "body": f"<!-- colorcorrect-source:{SHA} -->\n"
                + release.policy_marker(stable, include_macos_arm64), "target_commitish": SHA,
                "draft": draft, "prerelease": not stable}

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
                self.values = {"/actions/runs/123": {"head_sha": SHA, "head_branch": TAG, "event": "push", "path": ".github/workflows/native-builds.yml",
                                                     "repository": {"full_name": "owner/repo"}},
                               "/git/ref/tags/" + TAG: {"object": {"type": "commit", "sha": SHA}},
                               "/compare/" + SHA + "...main": {"behind_by": 0, "merge_base_commit": {"sha": SHA}}}
            def request(self, path):
                return self.values[path.removeprefix(self.prefix)]
            def pages(self, resource, key):
                return iter([{"name":"Validation result","status":"completed","conclusion":"success"}])
        api = SourceAPI()
        release.verify_source(api, 123, SHA, TAG)
        for endpoint, field, value in (("/actions/runs/123", "head_sha", "b" * 40),
                                       ("/actions/runs/123", "head_branch", "main"),
                                       ("/git/ref/tags/" + TAG, "object", {"type": "commit", "sha": "b" * 40}),
                                       ("/compare/" + SHA + "...main", "behind_by", 1)):
            with self.subTest(endpoint=endpoint):
                broken = SourceAPI()
                broken.values[endpoint][field] = value
                with self.assertRaises(ValueError):
                    release.verify_source(broken, 123, SHA, TAG)
        for rows in ([],[{"name":"Validation result","status":"completed","conclusion":"failure"}],
                     [{"name":"Validation result","status":"in_progress","conclusion":None}]):
            with self.subTest(gates=rows), patch.object(api,"pages",return_value=iter(rows)):
                with self.assertRaisesRegex(ValueError,"aggregate validation"):
                    release.verify_source(api,123,SHA,TAG)

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
                 {"manifest_change": {"worker_features_declared": None}},
                 {"manifest_change": {"worker_features_declared": ["ort-plane", "wgpu-lut", "rust-inference", "rust-inference"]}},
                 {"manifest_change": {"source": None}},
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
            files = self.staged_files(Path(temporary))
            api = FakeGitHub()
            result = release.publish(api, 123, SHA, TAG, files)
            self.assertEqual([operation[1] for operation in api.writes], ["POST"] * 18 + ["PATCH"])
            self.assertTrue(api.writes[0][2]["draft"])
            self.assertTrue(api.writes[0][2]["prerelease"])
            self.assertEqual(api.writes[0][2]["make_latest"], "false")
            self.assertEqual(api.writes[-1][2], {"draft": False, "prerelease": True, "make_latest": "false"})
            self.assertFalse(result["draft"])
            self.assertEqual(source.call_count, 2)

    @patch("publish_release.verify_source")
    def test_published_assets_are_never_replaced_or_added(self, source):
        with tempfile.TemporaryDirectory() as temporary:
            files = self.staged_files(Path(temporary))
            previous = self.previous(draft=False)
            assets = [self.asset(file) for file in files]
            api = FakeGitHub(previous, assets)
            release.publish(api, 123, SHA, TAG, files)
            self.assertEqual(api.writes, [])
            for broken in ([], [dict(assets[0], digest="sha256:" + "0" * 64)] + assets[1:],
                           [dict(assets[0], name="unexpected.dmg")] + assets[1:]):
                api = FakeGitHub(previous, broken)
                with self.assertRaises(ValueError):
                    release.publish(api, 123, SHA, TAG, files)
                self.assertEqual(api.writes, [])

    @patch("publish_release.verify_source")
    def test_resume_own_draft_reuses_only_identical_existing_assets(self, source):
        with tempfile.TemporaryDirectory() as temporary:
            files = self.staged_files(Path(temporary))
            previous = self.previous(draft=True)
            asset = self.asset(files[0])
            api = FakeGitHub(previous, [asset])
            release.publish(api, 123, SHA, TAG, files)
            self.assertEqual([operation[3] for operation in api.writes if operation[1] == "POST"], [file.name for file in files[1:]])
            self.assertEqual(api.writes[-1][1], "PATCH")

    def test_mac_binary_requires_explicit_opt_in_and_binary_manifest(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "mac.zip"
            candidate(archive, release.MAC_RID)
            output = root / "out"; output.mkdir()
            with self.assertRaisesRegex(ValueError, "explicit opt-in"):
                release.stage_candidate(archive, output, "0.2.0", release.MAC_RID, SHA)
            files = release.stage_candidate(archive, output, "0.2.0", release.MAC_RID, SHA, include_macos_arm64=True)
            self.assertEqual(len(files), 4)
        for change in ({"distribution_policy": "source only"}, {"architecture": "x64"},
                       {"minimum_os": "macOS 11.0"}, {"signing": {"kind": "none", "notarized": False}},
                       {"signing": {"kind": "ad-hoc", "notarized": 0}}):
            with self.subTest(change=change), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary); output = root / "out"; output.mkdir()
                archive = root / "mac.zip"; candidate(archive, release.MAC_RID, manifest_change=change)
                with self.assertRaises(ValueError):
                    release.stage_candidate(archive, output, "0.2.0", release.MAC_RID, SHA, include_macos_arm64=True)
        for rid in ("osx-x64", "macos", "linux-unknown"):
            with self.subTest(rid=rid), self.assertRaisesRegex(ValueError, "Unsupported"):
                release.distribution_names("0.2.0", rid)

    def test_mac_enabled_run_requires_both_archives_and_all_twenty_one_assets(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            api = self.artifact_api(root, True)
            with self.assertRaisesRegex(ValueError, "distribution policy"):
                release.stage_run(api, 123, SHA, "0.2.0", root / "source-only")
            files = release.stage_run(api, 123, SHA, "0.2.0", root / "binary", include_macos_arm64=True)
            self.assertEqual(len(files), 21)
            self.assertEqual({f.name for f in files}, release.release_asset_names("0.2.0", True))
            release.verify_local_assets(files, "0.2.0", SHA, True)
            for dropped in (files[0], next(f for f in files if f.name.endswith("osx-arm64.tar.gz"))):
                with self.assertRaisesRegex(ValueError, "complete allowlisted"):
                    release.verify_local_assets([f for f in files if f != dropped], "0.2.0", SHA, True)
            with self.assertRaisesRegex(ValueError, "complete allowlisted"):
                release.verify_local_assets(files, "0.2.0", SHA, False)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); archive = root / "mac.zip"; output = root / "out"; output.mkdir()
            candidate(archive, release.MAC_RID)
            with zipfile.ZipFile(archive) as z:
                members = {n: z.read(n) for n in z.namelist() if not n.endswith(".tar.gz")}
            with zipfile.ZipFile(archive, "w") as z:
                for n, data in members.items():z.writestr(n, data)
            with self.assertRaisesRegex(ValueError, "missing"):
                release.stage_candidate(archive, output, "0.2.0", release.MAC_RID, SHA, include_macos_arm64=True)

    @patch("publish_release.verify_source")
    def test_explicit_stable_mac_release_publishes_full_latest_only_when_complete(self, source):
        with tempfile.TemporaryDirectory() as temporary:
            files = self.staged_files(Path(temporary), True)
            api = FakeGitHub()
            result = release.publish(api, 123, SHA, TAG, files, stable=True, include_macos_arm64=True)
            self.assertTrue(api.writes[0][2]["draft"])
            self.assertFalse(api.writes[0][2]["prerelease"])
            self.assertEqual(api.writes[0][2]["make_latest"], "false")
            self.assertNotIn("preview", api.writes[0][2]["name"])
            self.assertIn("ad-hoc signed, not notarized", api.writes[0][2]["body"])
            self.assertEqual(api.writes[-1][2], {"draft": False, "prerelease": False, "make_latest": "true"})
            self.assertFalse(result["draft"]);self.assertFalse(result["prerelease"])
            self.assertEqual(len(api.assets),21)
            self.assertEqual(source.call_count,2)

    @patch("publish_release.verify_source")
    def test_draft_rejects_all_bad_existing_assets_before_uploading_any_missing(self, source):
        with tempfile.TemporaryDirectory() as temporary:
            files = self.staged_files(Path(temporary), True)
            good = self.asset(files[-1])
            for assets in ([dict(good,digest="sha256:"+"0"*64)], [dict(good,state="starter")],
                           [good,good], [dict(good,name="unexpected.dmg")], [{"name":None}]):
                with self.subTest(assets=assets):
                    api=FakeGitHub(self.previous(draft=True,stable=True,include_macos_arm64=True),assets)
                    with self.assertRaises(ValueError):
                        release.publish(api,123,SHA,TAG,files,stable=True,include_macos_arm64=True)
                    self.assertEqual(api.writes,[])

    @patch("publish_release.verify_source")
    def test_draft_rejects_changed_policy_or_malformed_identity_without_mutation(self, source):
        with tempfile.TemporaryDirectory() as temporary:
            files=self.staged_files(Path(temporary),True)
            previous=self.previous(draft=True,stable=True,include_macos_arm64=True)
            cases=[dict(previous,prerelease=True),dict(previous,draft=None),dict(previous,id=True),dict(previous,body=[]),
                   dict(previous,target_commitish="main"),dict(previous,body=previous['body']+f"<!-- colorcorrect-source:{SHA} -->"),
                   dict(previous,body=f"<!-- colorcorrect-source:{SHA} -->"+release.policy_marker(True,False))]
            for item in cases:
                api=FakeGitHub(item)
                with self.assertRaises(ValueError):release.publish(api,123,SHA,TAG,files,stable=True,include_macos_arm64=True)
                self.assertEqual(api.writes,[])

    @patch("publish_release.verify_source")
    def test_partial_stable_upload_recovery_reuses_verified_bytes_and_never_replaces(self, source):
        class Interrupted(FakeGitHub):
            def request(self,path,**kwargs):
                if kwargs.get('upload') is not None and len(self.assets)==2:raise RuntimeError('upload interrupted')
                return super().request(path,**kwargs)
        with tempfile.TemporaryDirectory() as temporary:
            files=self.staged_files(Path(temporary),True)
            api=Interrupted()
            with self.assertRaisesRegex(RuntimeError,'interrupted'):
                release.publish(api,123,SHA,TAG,files,stable=True,include_macos_arm64=True)
            self.assertTrue(api.previous['draft']);self.assertEqual(len(api.assets),2)
            self.assertFalse(any(operation[1]=='PATCH' for operation in api.writes))
            existing={asset['name'] for asset in api.assets}
            recovered=FakeGitHub(api.previous,api.assets)
            release.publish(recovered,123,SHA,TAG,files,stable=True,include_macos_arm64=True)
            self.assertFalse(existing & {op[3] for op in recovered.writes if op[1]=='POST'})
            self.assertEqual(len(recovered.assets),21);self.assertFalse(recovered.previous['draft'])

    @patch("publish_release.verify_source")
    def test_uploaded_assets_are_relisted_before_publication(self, source):
        class Corrupted(FakeGitHub):
            def pages(self,resource,key=None):
                if resource.endswith('/assets') and len(self.assets)==21:
                    self.assets[-1]=dict(self.assets[-1],digest='sha256:'+'0'*64)
                return super().pages(resource,key)
        with tempfile.TemporaryDirectory() as temporary:
            files=self.staged_files(Path(temporary),True);api=Corrupted()
            with self.assertRaisesRegex(ValueError,'not identical'):
                release.publish(api,123,SHA,TAG,files,stable=True,include_macos_arm64=True)
            self.assertTrue(api.previous['draft']);self.assertFalse(any(op[1]=='PATCH' for op in api.writes))

    @patch("publish_release.verify_source")
    def test_published_stable_is_read_only_and_latest_is_confirmed(self, source):
        with tempfile.TemporaryDirectory() as temporary:
            files=self.staged_files(Path(temporary),True)
            previous=self.previous(draft=False,stable=True,include_macos_arm64=True)
            api=FakeGitHub(previous,[self.asset(f) for f in files])
            release.publish(api,123,SHA,TAG,files,stable=True,include_macos_arm64=True)
            self.assertEqual(api.writes,[])
            # A published preview may never be promoted or overwritten implicitly.
            api=FakeGitHub(dict(previous,prerelease=True),api.assets)
            with self.assertRaises(ValueError):release.publish(api,123,SHA,TAG,files,stable=True,include_macos_arm64=True)
            self.assertEqual(api.writes,[])

    @patch("publish_release.time.sleep")
    @patch("publish_release.verify_source")
    def test_latest_confirmation_does_not_promote_an_older_already_published_release(self, source, sleep):
        class NotLatest(FakeGitHub):
            def request(self,path,**kwargs):
                if path.endswith('/releases/latest'):return {'id':18,'tag_name':'v9.0.0'}
                return super().request(path,**kwargs)
        with tempfile.TemporaryDirectory() as temporary:
            files=self.staged_files(Path(temporary),True)
            previous=self.previous(draft=False,stable=True,include_macos_arm64=True)
            api=NotLatest(previous,[self.asset(f) for f in files])
            with self.assertRaisesRegex(ValueError,'as latest'):
                release.publish(api,123,SHA,TAG,files,stable=True,include_macos_arm64=True)
            self.assertEqual(api.writes,[])
            self.assertEqual(sleep.call_count,2)


if __name__ == "__main__":
    unittest.main()
