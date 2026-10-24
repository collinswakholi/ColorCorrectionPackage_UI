"""Parse prepared workflows offline; never dispatch jobs or contact GitHub."""
from pathlib import Path
import re
import unittest
import yaml

ROOT = Path(__file__).resolve().parents[2]


class UniqueLoader(yaml.BaseLoader):
    """Keep Actions scalars (including 'on') literal and reject hidden duplicate keys."""


def mapping(loader, node):
    result = {}
    for key_node, value_node in node.value:
        key = loader.construct_object(key_node)
        if key in result:
            raise ValueError(f"Duplicate workflow key: {key}")
        result[key] = loader.construct_object(value_node)
    return result


UniqueLoader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, mapping)


def load(name):
    return yaml.load((ROOT / ".github/workflows" / name).read_text(), Loader=UniqueLoader)


class WorkflowContracts(unittest.TestCase):
    def test_native_targets_pins_and_source_filters(self):
        workflow = load("native-builds.yml")
        matrix = workflow["jobs"]["packages"]["strategy"]["matrix"]["include"]
        actual = {(row["runner"], row["rid"], row["architecture"], row["rust"]) for row in matrix}
        self.assertEqual(actual, {
            ("windows-2025", "win-x64", "x64", "x86_64-pc-windows-msvc"),
            ("windows-11-arm", "win-arm64", "arm64", "aarch64-pc-windows-msvc"),
            ("ubuntu-24.04", "linux-x64", "x64", "x86_64-unknown-linux-gnu"),
            ("ubuntu-24.04-arm", "linux-arm64", "arm64", "aarch64-unknown-linux-gnu"),
        })
        self.assertEqual(workflow["env"]["CARGO_INCREMENTAL"], "0")
        filters = set(workflow["on"]["pull_request"]["paths"])
        self.assertTrue({"*", "docs/**", "notebooks/**", "licenses/**", "test_images/**",
                         "testdata/acceptance/**", ".github/**"} <= filters)
        for job in workflow["jobs"].values():
            for step in job["steps"]:
                if step.get("uses", "").startswith("actions/setup-python@"):
                    self.assertEqual(step["with"]["python-version"], "3.12.10")
                if step.get("uses", "").startswith("actions/setup-dotnet@"):
                    self.assertEqual(step["with"], {"global-json-file": "global.json"})

    def test_readonly_builds_full_action_pins_and_scoped_release_permissions(self):
        for name in ("native-builds.yml", "hardware-gpu-acceptance.yml"):
            workflow = load(name)
            self.assertEqual(workflow["permissions"], {"contents": "read"})
            for job_name, job in workflow["jobs"].items():
                if "permissions" in job:
                    expected = {"contents": "write", "actions": "read"} if name == "native-builds.yml" and job_name == "publish" else {"contents": "read"}
                    self.assertEqual(job["permissions"], expected)
                for step in job["steps"]:
                    if "uses" in step:
                        self.assertRegex(step["uses"], r"^actions/(checkout|setup-python|setup-dotnet|upload-artifact)@[0-9a-f]{40}$")
                    self.assertNotRegex(step.get("run", ""), r"(?im)\b(gh\s+release|twine\s+upload|cargo\s+publish|npm\s+publish|docker\s+push|fetch_libtorch|install_libtorch)\b")
            self.assertNotIn("pull_request_target", workflow["on"])

    def test_only_version_tag_pushes_publish_after_all_native_jobs(self):
        workflow = load("native-builds.yml")
        self.assertEqual(workflow["on"]["push"], {"branches": ["main"], "tags": ["v*"]})
        publish = workflow["jobs"]["publish"]
        self.assertEqual(set(publish["needs"]), {"packages", "macos_source"})
        checkout = next(step for step in publish["steps"] if step.get("uses", "").startswith("actions/checkout@"))
        self.assertEqual(checkout["with"], {"persist-credentials": "false"})
        for condition in ("github.event_name == 'push'", "startsWith(github.ref, 'refs/tags/v')",
                          "needs.packages.result == 'success'", "needs.macos_source.result == 'success'"):
            self.assertIn(condition, publish["if"])
        for name, job in workflow["jobs"].items():
            for step in job["steps"]:
                if "GH_TOKEN" in step.get("env", {}):
                    self.assertEqual(name, "publish")
                    self.assertEqual(step["env"]["GH_TOKEN"], "${{ github.token }}")
                    self.assertEqual(step["run"], 'python scripts/publish_release.py --publish --output "$RUNNER_TEMP/release-assets"')
                if "publish_release.py" in step.get("run", ""):
                    self.assertEqual(name, "publish")

    def test_ci_uses_the_installed_pinned_native_rust_toolchain(self):
        workflow = load("native-builds.yml")
        channel = re.search(r'(?m)^channel\s*=\s*"([^"]+)"',
                            (ROOT / "rust-toolchain.toml").read_text())[1]
        self.assertRegex(channel, r"^\d+\.\d+\.\d+$")
        for name, suffix in (("packages", "${{ matrix.rust }}"),
                             ("macos_source", "aarch64-apple-darwin")):
            with self.subTest(job=name):
                job = workflow["jobs"][name]
                # Environment overrides the local optional rustfmt/clippy component
                # request while retaining the exact compiler and native host target.
                self.assertEqual(job["env"]["RUSTUP_TOOLCHAIN"], channel + "-" + suffix)
                setup = next(step["run"] for step in job["steps"]
                             if "rustup toolchain install" in step.get("run", ""))
                self.assertIn('rustup toolchain install "$RUSTUP_TOOLCHAIN" --profile minimal', setup)
                self.assertIn('rustup default "$RUSTUP_TOOLCHAIN"', setup)
                self.assertIn("rustup show active-toolchain", setup)
                self.assertIn("rustc -Vv", setup)

    def test_source_only_mac_artifacts_and_separate_hardware_gate(self):
        workflow = load("native-builds.yml")
        mac = workflow["jobs"]["macos_source"]
        self.assertEqual(mac["runs-on"], "macos-15")
        for step in mac["steps"]:
            if step.get("uses", "").startswith("actions/setup-python@"):
                self.assertEqual(step["with"]["architecture"], "arm64")
            self.assertNotIn("--install-package", step.get("run", ""))
            if step.get("uses", "").startswith("actions/upload-artifact@"):
                paths = step["with"]["path"].splitlines()
                self.assertTrue(paths)
                self.assertTrue(all(path.startswith("artifacts/native-ci/osx-arm64/evidence/")
                                    and path.endswith((".json", ".log", ".txt", ".trx")) for path in paths))
        hardware = load("hardware-gpu-acceptance.yml")
        self.assertEqual(set(hardware["on"]), {"workflow_dispatch"})
        self.assertEqual(hardware["on"]["workflow_dispatch"]["inputs"]["enable_hardware_run"]["default"], "false")
        gate = hardware["jobs"]["acceptance"]
        self.assertIn("inputs.enable_hardware_run", gate["if"])
        self.assertIn("vars.CCP_GPU_RUNNER_LABEL != ''", gate["if"])
        self.assertEqual(gate["env"]["CCP_GPU_ACCEPTANCE_ENABLED"], "1")
        for step in gate["steps"]:
            if step.get("uses", "").startswith("actions/upload-artifact@"):
                self.assertTrue(all(path.endswith((".json", ".log", ".txt", ".trx"))
                                    for path in step["with"]["path"].splitlines()))

    def test_duplicate_workflow_keys_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "Duplicate workflow key"):
            yaml.load("permissions:\n  contents: read\n  contents: write\n", Loader=UniqueLoader)


if __name__ == "__main__":
    unittest.main()
