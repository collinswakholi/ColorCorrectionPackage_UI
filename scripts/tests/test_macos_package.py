"""Mac distribution contracts with synthetic Mach-O/tools, not native acceptance."""
from pathlib import Path
import json
import os
import plistlib
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile

SCRIPTS = Path(__file__).parents[1]
sys.path.insert(0, str(SCRIPTS))
import package_macos as mac
from package_support import digest
import test_macos_source as source_fixtures
from test_package_native import executable, universal_macho
from stage_macos_source import stage


class MacPackageTests(unittest.TestCase):
    def inputs(self, root):
        source = source_fixtures.MacSourceTests().inputs(root)
        stage(root, source)
        (source / "app/ColorCorrect.App.deps.json").write_text("{}")
        (source / "app/System.Web.dll").write_bytes(b"managed PE resource fixture")
        assets = root / "app/ColorCorrect.App/Assets"; assets.mkdir()
        shutil.copy2(SCRIPTS.parent / "app/ColorCorrect.App/Assets/app.png", assets / "app.png")
        return source

    def tools(self):
        calls, replacements, ids, paths = [], {}, {}, {}

        def invoke(command, **kwargs):
            command = list(map(str, command)); calls.append(command); out = ""
            if command[0] == "sw_vers": out = "14.7\n"
            elif command[0] == "xcrun": Path(command[-1]).write_bytes(executable("Darwin", "arm64"))
            elif command[0] == "lipo": Path(command[-1]).write_bytes(executable("Darwin", "arm64"))
            elif command[0] == "otool":
                owner = command[-1]; name = Path(owner).name
                own = ids.get(owner, "@rpath/libonnxruntime.1.24.4.dylib" if name == "libonnxruntime.dylib" else "@rpath/" + name if name.endswith(".dylib") else None)
                if command[1] == "-D": out = owner + ":\n" + (own + "\n" if own else "")
                elif command[1] == "-L":
                    deps = ["@rpath/libonnxruntime.1.24.4.dylib"] if "providers_webgpu" in name else ["/usr/lib/libSystem.B.dylib"]
                    deps = [replacements.get((owner, value), value) for value in deps]
                    out = owner + ":\n" + "".join("\t" + value + " (compatibility version 1.0.0, current version 65535.0.0)\n" for value in ([own] if own else []) + deps)
                else:
                    out = "Load command 0\n cmd LC_BUILD_VERSION\n cmdsize 32\n platform 1\n minos 14.0\n sdk 26.0\nLoad command 1\n cmd LC_ID_DYLIB\n current version 65535.0.0\n"
                    if paths.get(owner, True): out += "Load command 2\n cmd LC_RPATH\n cmdsize 40\n path /Users/build/developer-prefix (offset 12)\n"
            elif command[0] == "install_name_tool":
                if command[1] == "-change": replacements[(command[-1], command[2])] = command[3]
                elif command[1] == "-id": ids[command[-1]] = command[2]
                elif command[1] == "-delete_rpath": paths[command[-1]] = False
            elif command[0] == "codesign" and "--sign" in command:
                target = Path(command[-1])
                if target.is_dir():
                    main = target / "Contents/MacOS" / mac.EXECUTABLE
                    main.write_bytes(main.read_bytes() + b"outer-signature")
                    signatures = target / "Contents/_CodeSignature"; signatures.mkdir()
                    (signatures / "CodeResources").write_text("signed resource envelope")
                else: target.write_bytes(target.read_bytes() + b"native-signature")
            elif command[0] != "codesign": raise AssertionError(command)
            return subprocess.CompletedProcess(command, 0, stdout=out, stderr="")
        return invoke, calls

    def test_explicit_gate_and_native_architecture(self):
        with patch.object(sys, "argv", ["package_macos.py"]), self.assertRaises(SystemExit):
            mac.main()
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); source = self.inputs(root)
            with patch.object(mac.platform, "system", return_value="Darwin"), patch.object(mac.platform, "machine", return_value="x86_64"):
                with self.assertRaisesRegex(ValueError, "Intel and Rosetta"):
                    mac.build(root, source, root / "output", root / "colorcorrect_studio-2.2.1-py3-none-any.whl", root / "dependency-notices", False)

    def test_bundle_archives_manifest_signing_and_relocation(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); source = self.inputs(root); output = root / "binary output"
            worker_before = digest(source / "ccp-engine"); runtime_before = digest(source / "libonnxruntime.dylib")
            # Materialization strips the Intel slice rather than distributing it.
            (source / "app/libAvaloniaNative.dylib").write_bytes(universal_macho())
            fake, calls = self.tools()
            with patch.object(mac.platform, "system", return_value="Darwin"), patch.object(mac.platform, "machine", return_value="arm64"), patch.object(mac, "run", side_effect=fake):
                result = mac.build(root, source, output, root / "colorcorrect_studio-2.2.1-py3-none-any.whl", root / "dependency-notices", False)
            bundle = output / "payload" / mac.APP_NAME; runtime = bundle / "Contents/Resources/runtime"
            launcher = bundle / "Contents/MacOS" / mac.EXECUTABLE
            self.assertEqual(result["distribution_policy"], "application")
            self.assertEqual(result["signing"], {"kind": "ad-hoc", "notarized": False})
            self.assertEqual(result["minimum_os"], "macOS 14.0")
            self.assertEqual(result["payload_root"], mac.APP_NAME + "/Contents/Resources/runtime")
            self.assertEqual(list((bundle / "Contents/MacOS").iterdir()), [launcher])
            self.assertEqual((runtime / "app/System.Web.dll").read_bytes(), b"managed PE resource fixture")
            self.assertTrue((runtime / "models/manifest.json").is_file())
            self.assertTrue((runtime / "python/colorcorrect_studio-2.2.1-py3-none-any.whl").is_file())
            self.assertTrue((runtime / "licenses/dependencies/manifest.json").is_file())
            for helper in ("setup", "check-system"):
                self.assertEqual(mac.executable_architectures(runtime / helper), {("Darwin", "arm64")})
                self.assertFalse((runtime / helper).read_bytes().startswith(b"#!"))
                self.assertTrue(any(command[0] == "codesign" and "--sign" in command and Path(command[-1]).resolve() == (runtime / helper).resolve() for command in calls))
            info = plistlib.loads((bundle / "Contents/Info.plist").read_bytes())
            self.assertEqual(info["CFBundleExecutable"], mac.EXECUTABLE)
            self.assertEqual(info["LSArchitecturePriority"], ["arm64"])
            self.assertEqual(info["CFBundleVersion"], "2.0.0")
            self.assertEqual(digest(source / "ccp-engine"), worker_before)
            self.assertEqual(digest(source / "libonnxruntime.dylib"), runtime_before)
            self.assertIn("transformations", json.loads((runtime / "gpu-runtime-manifest.json").read_text()))
            for name, sha in result["sha256"].items(): self.assertEqual(digest(output / "payload" / name), sha)
            inner = json.loads((runtime / "package-manifest.json").read_text())
            self.assertNotIn(mac.EXECUTABLE, inner["sha256"])
            for name, sha in inner["sha256"].items(): self.assertEqual(digest(runtime / name), sha)
            self.assertTrue(any(command[0] == "lipo" and Path(command[1]).resolve() == (runtime / "app/libAvaloniaNative.dylib").resolve() for command in calls))
            self.assertTrue(any(command[0:2] == ["install_name_tool", "-change"] and command[3] == "@loader_path/libonnxruntime.dylib" for command in calls))
            self.assertTrue(any(command[:2] == ["codesign", "--verify"] for command in calls))
            archives = [output / "ColorCorrect-Studio-2.0.0-osx-arm64.zip", output / "ColorCorrect-Studio-2.0.0-osx-arm64.tar.gz"]
            for path in archives: self.assertTrue(path.is_file(), path)
            with zipfile.ZipFile(archives[0]) as archive:
                info = archive.getinfo(mac.APP_NAME + "/Contents/MacOS/" + mac.EXECUTABLE)
                self.assertEqual(stat.S_IMODE(info.external_attr >> 16), stat.S_IMODE(launcher.stat().st_mode))
                if os.name != "nt": self.assertEqual(stat.S_IMODE(info.external_attr >> 16) & 0o111, 0o111)
                self.assertFalse(any(stat.S_ISLNK(item.external_attr >> 16) for item in archive.infolist()))
                self.assertTrue(all(name.startswith(mac.APP_NAME + "/") for name in archive.namelist()))
            with tarfile.open(archives[1]) as archive:
                self.assertFalse(any(member.issym() or member.islnk() for member in archive))
            for line in (output / "SHA256SUMS").read_text().splitlines():
                sha, name = line.split("  "); self.assertEqual(digest(output / name), sha)

    def test_unresolved_dependencies_and_wrong_architecture_are_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); node = root / "worker"; node.write_bytes(executable("Darwin", "x64"))
            with self.assertRaisesRegex(ValueError, "Non-Apple-silicon"):
                mac.relocate_native(root)
            node.write_bytes(executable("Darwin", "arm64"))
            with self.assertRaisesRegex(ValueError, "Unresolved"):
                mac.resolve_private("/opt/homebrew/lib/unbundled.dylib", node, root, {})
            self.assertFalse(mac.system_library("/usr/lib/../local/private.dylib"))

    def test_corrupt_runtime_and_existing_output_are_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); source = self.inputs(root); output = root / "output"; output.mkdir()
            fake, calls = self.tools()
            with patch.object(mac.platform, "system", return_value="Darwin"), patch.object(mac.platform, "machine", return_value="arm64"), patch.object(mac, "run", side_effect=fake):
                with self.assertRaises(FileExistsError):
                    mac.build(root, source, output, root / "colorcorrect_studio-2.2.1-py3-none-any.whl", root / "dependency-notices", False)
                (source / "libonnxruntime.dylib").write_bytes(b"corrupt")
                with self.assertRaisesRegex(ValueError, "checksum mismatch"):
                    mac.build(root, source, root / "fresh", root / "colorcorrect_studio-2.2.1-py3-none-any.whl", root / "dependency-notices", False)
            self.assertFalse((root / "fresh").exists())

    def test_launcher_contains_no_build_path_or_external_runtime_fallback(self):
        self.assertNotIn("x-terminal-emulator", mac.LAUNCHER)
        self.assertNotIn("/Users/", mac.LAUNCHER)
        self.assertIn("_NSGetExecutablePath", mac.LAUNCHER)
        self.assertIn('../Resources/runtime', mac.LAUNCHER)
        self.assertIn('dateWithTimeIntervalSinceNow:120', mac.LAUNCHER)
        self.assertIn('reply[@"setup_verified"]', mac.LAUNCHER)
        self.assertIn('record[@"exit_code"]', mac.LAUNCHER)
        self.assertIn('BOOL cpuOnly=[mode isEqual:@"cpu"]', mac.LAUNCHER)
        self.assertIn('@"detection_policy":cpuOnly?@"cpu-inference":@"auto"', mac.LAUNCHER)
        self.assertIn('@"correction_policy":cpuOnly?@"cpu":@"auto"', mac.LAUNCHER)
        self.assertNotIn("xattr", mac.LAUNCHER)


if __name__ == "__main__": unittest.main()
