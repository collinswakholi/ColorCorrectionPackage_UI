"""Relocated binary acceptance contracts; synthetic files do not certify macOS."""
from pathlib import Path
import hashlib
import json
import plistlib
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from native_ci_acceptance import verify_macos_candidate, verify_macos_runtime


class MacBinaryAcceptance(unittest.TestCase):
    def test_both_archives_relocate_and_verify_all_bytes_and_signatures(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            package = work / "package"
            package.mkdir()
            payload = work / "built"
            executable = payload / "ColorCorrect Studio.app/Contents/MacOS/ColorCorrect-Studio"
            executable.parent.mkdir(parents=True)
            executable.write_bytes(b"synthetic native fixture")
            relative = executable.relative_to(payload).as_posix()
            manifest = {"rid": "osx-arm64", "version": "0.2.2", "distribution_policy": "application",
                        "payload_root": "ColorCorrect Studio.app/Contents/MacOS",
                        "sha256": {relative: hashlib.sha256(executable.read_bytes()).hexdigest()}}
            (package / "package-manifest.json").write_text(json.dumps(manifest))
            stem = "ColorCorrect-Studio-0.2.2-osx-arm64"
            with zipfile.ZipFile(package / (stem + ".zip"), "w") as archive:
                archive.write(executable, relative)
            with tarfile.open(package / (stem + ".tar.gz"), "w:gz") as archive:
                archive.add(payload / "ColorCorrect Studio.app", arcname="ColorCorrect Studio.app")
            commands = []

            def run(command):
                commands.append(command)
                if command[0] == "ditto":
                    shutil.copytree(payload, command[-1], dirs_exist_ok=True)

            relocated, receipt = verify_macos_candidate(package, work, "0.2.2", run)
            self.assertEqual(relocated, work / "Relocated Applications/ColorCorrect Studio.app/Contents/MacOS")
            self.assertEqual(len(receipt["archives"]), 2)
            self.assertEqual(sum(command[0] == "codesign" for command in commands), 2)
            executable.write_bytes(b"changed")
            # The distributed copy, not the source tree, remains the execution root.
            self.assertEqual((relocated / "ColorCorrect-Studio").read_bytes(), b"synthetic native fixture")

    def test_source_only_manifest_cannot_become_a_binary_candidate(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            (work / "package-manifest.json").write_text(json.dumps({"rid": "osx-arm64", "version": "0.2.2"}))
            with self.assertRaisesRegex(RuntimeError, "delivery contract"):
                verify_macos_candidate(work, work, "0.2.2", lambda command: self.fail("must reject before execution"))

    def test_helpers_require_the_relocated_root_and_a_successful_cpu_exit(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            payload = work / "Moved App/Contents/MacOS"
            payload.mkdir(parents=True)
            preferences = work / "macos-setup-preferences/colorcorrect/setup-state.json"
            preferences.parent.mkdir(parents=True)
            preferences.write_text(json.dumps({"mode": "cpu", "components": ["cpu"],
                                               "detection_policy": "cpu-inference", "correction_policy": "cpu"}))
            location = {"root": str(payload), "architecture": "arm64"}
            probe = {"probes": [{"policy": "cpu-inference", "status": "verified", "exit_code": 0}]}
            replies = [subprocess.CompletedProcess([], 0, json.dumps(value), "") for value in (location, probe)]
            with patch("native_ci_acceptance.subprocess.run", side_effect=replies) as run:
                self.assertEqual(verify_macos_runtime(payload, work, work, {})["status"], "passed")
                self.assertEqual(run.call_args.kwargs["timeout"], 180)
                self.assertEqual(run.call_args.args[0][0], str(payload / "setup"))
            probe["probes"][0]["exit_code"] = 1
            replies = [subprocess.CompletedProcess([], 0, json.dumps(value), "") for value in (location, probe)]
            with patch("native_ci_acceptance.subprocess.run", side_effect=replies):
                with self.assertRaisesRegex(RuntimeError, "complete CPU"):
                    verify_macos_runtime(payload, work, work, {})
            location["root"] = str(work / "unrelated source")
            replies = [subprocess.CompletedProcess([], 0, json.dumps(value), "") for value in (location, probe)]
            with patch("native_ci_acceptance.subprocess.run", side_effect=replies):
                with self.assertRaisesRegex(RuntimeError, "wrong private root"):
                    verify_macos_runtime(payload, work, work, {})


if __name__ == "__main__":
    unittest.main()
