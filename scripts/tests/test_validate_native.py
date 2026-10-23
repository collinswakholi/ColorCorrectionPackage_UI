"""Native release gates must distinguish host architecture from emulation."""
import importlib.util
import io
import json
from contextlib import redirect_stderr
from pathlib import Path
import sys
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from test_package_native import executable

SCRIPTS = Path(__file__).parents[1]
sys.path.insert(0, str(SCRIPTS))
spec = importlib.util.spec_from_file_location("validate_native", SCRIPTS / "validate_native.py")
validator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(validator)


class NativeValidationGates(unittest.TestCase):
    def test_wrong_worker_architecture_rejected_before_commands(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            interpreter = root / "python.exe"
            interpreter.write_bytes(executable("Windows", "arm64"))
            worker = root / "ccp-engine.exe"
            worker.write_bytes(executable("Windows", "x64"))
            error = io.StringIO()
            with patch.object(sys, "argv", ["validate_native.py", "--output", str(root / "report"), "--engine", str(worker)]), \
                 patch.object(sys, "executable", str(interpreter)), \
                 patch.object(validator.platform, "machine", return_value="ARM64"), \
                 patch.object(validator.platform, "system", return_value="Windows"), \
                 patch.object(validator.platform, "platform", return_value="mocked Windows ARM64 host"), \
                 patch.object(validator, "native_architecture", return_value="arm64"), \
                 patch.object(validator.shutil, "which") as which, \
                 patch.object(validator.subprocess, "run") as run, redirect_stderr(error):
                with self.assertRaises(SystemExit):
                    validator.main()
                run.assert_not_called()
                which.assert_not_called()
            self.assertIn("native architecture does not match", error.getvalue())
            self.assertIn("ccp-engine.exe", error.getvalue())

    def test_explicit_and_bare_dotnet_hosts_reject_wrong_architecture_before_commands(self):
        for explicit in (False, True):
            with self.subTest(explicit=explicit), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                interpreter = root / "python.exe"
                interpreter.write_bytes(executable("Windows", "arm64"))
                dotnet = root / "dotnet.exe"
                dotnet.write_bytes(executable("Windows", "x64"))
                command = str(dotnet) if explicit else "dotnet"
                error = io.StringIO()
                with patch.object(sys, "argv", ["validate_native.py", "--output", str(root / "report"), "--dotnet", command]), \
                     patch.object(sys, "executable", str(interpreter)), \
                     patch.object(validator.platform, "machine", return_value="ARM64"), \
                     patch.object(validator.platform, "system", return_value="Windows"), \
                     patch.object(validator.platform, "platform", return_value="mocked Windows ARM64 host"), \
                     patch.object(validator, "native_architecture", return_value="arm64"), \
                     patch.object(validator.shutil, "which", return_value=str(dotnet)) as which, \
                     patch.object(validator.subprocess, "run") as run, redirect_stderr(error):
                    with self.assertRaises(SystemExit):
                        validator.main()
                    run.assert_not_called()
                    which.assert_called_once_with(command)
                self.assertIn("native architecture does not match", error.getvalue())
                self.assertIn("dotnet.exe", error.getvalue())

    def test_matching_hosts_invoke_the_inspected_dotnet_and_keep_release_pending(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            binaries = []
            for name in ("python.exe", "ccp-engine.exe", "dotnet.exe"):
                path = root / name
                path.write_bytes(executable("Windows", "arm64"))
                binaries.append(path)
            interpreter, worker, dotnet = binaries
            destination = root / "report"
            with patch.object(sys, "argv", ["validate_native.py", "--output", str(destination), "--engine", str(worker)]), \
                 patch.object(sys, "executable", str(interpreter)), \
                 patch.object(validator.platform, "machine", return_value="ARM64"), \
                 patch.object(validator.platform, "system", return_value="Windows"), \
                 patch.object(validator.platform, "platform", return_value="mocked Windows ARM64 host"), \
                 patch.object(validator, "native_architecture", return_value="arm64"), \
                 patch.object(validator.shutil, "which", return_value=str(dotnet)), \
                 patch.object(validator.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, stdout="mocked contract check")) as run:
                self.assertEqual(validator.main(), 0)
                self.assertEqual(run.call_count, 4)
                self.assertEqual(run.call_args_list[0].args[0][0], str(dotnet.resolve()))
            record = json.loads((destination / "native-validation.json").read_text(encoding="utf8"))
            self.assertTrue(record["native_host"])
            self.assertTrue(record["automated_checks_passed"])
            self.assertFalse(record["release_supported"])
            self.assertEqual(record["runtime_architectures"]["engine"], [["Windows", "arm64"]])
            self.assertEqual(record["runtime_architectures"]["dotnet"], [["Windows", "arm64"]])

    def test_x64_interpreter_on_arm_windows_is_not_native_acceptance(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            interpreter = root / "python.exe"
            interpreter.write_bytes(executable("Windows", "x64"))
            with patch.object(sys, "argv", ["validate_native.py", "--output", str(root / "report")]), \
                 patch.object(sys, "executable", str(interpreter)), \
                 patch.object(validator.platform, "machine", return_value="ARM64"), \
                 patch.object(validator.platform, "system", return_value="Windows"), \
                 patch.object(validator.platform, "platform", return_value="mocked Windows ARM64 host"), \
                 patch.object(validator, "native_architecture", return_value="arm64"), \
                 patch.object(validator.subprocess, "run") as run:
                with self.assertRaises(SystemExit):
                    validator.main()
                run.assert_not_called()

    def test_emulated_inventory_is_explicit_and_does_not_pass_acceptance(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            interpreter = root / "python.exe"
            interpreter.write_bytes(executable("Windows", "x64"))
            destination = root / "report"
            with patch.object(sys, "argv", ["validate_native.py", "--output", str(destination), "--inventory-only"]), \
                 patch.object(sys, "executable", str(interpreter)), \
                 patch.object(validator.platform, "machine", return_value="ARM64"), \
                 patch.object(validator.platform, "system", return_value="Windows"), \
                 patch.object(validator.platform, "platform", return_value="mocked Windows ARM64 host"), \
                 patch.object(validator, "native_architecture", return_value="arm64"), \
                 patch.object(validator.subprocess, "run") as run:
                self.assertEqual(validator.main(), 0)
                run.assert_not_called()
            record = json.loads((destination / "native-validation.json").read_text(encoding="utf8"))
            self.assertEqual(record["process_architecture"], "x64")
            self.assertEqual(record["target"], "win-arm64")
            self.assertFalse(record["native_host"])
            self.assertFalse(record["automated_checks_passed"])
            self.assertFalse(record["release_supported"])
