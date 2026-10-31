"""Native build/installed smoke; hardware GPU acceptance is a separate opt-in gate.

macOS builds source locally by default. Explicit --macos-binary additionally
packages and tests relocated Apple-silicon bundles. No release publication or
system driver installation is performed.
"""
from pathlib import Path, PurePosixPath
import argparse
import hashlib
import json
import os
import platform
import plistlib
import shutil
import subprocess
import sys
import tempfile
import tarfile
import time
import zipfile
from package_native import native_architecture, executable_architectures, TARGETS
from fetch_gpu_runtime import fetch, fetch_vc_redist
from package_support import native_version, source_identity


def rust_component_test_command(root: Path, rid: str, target_directory: Path | None = None) -> list:
    """Test every shipping crate; the Rust inference fallback ships only on Linux."""
    system = TARGETS[rid][0]
    packages = ["ccp_core", "ccp_detect", "ccp_bridge"]
    if system == "Linux":
        packages.append("ccp_wgpu_infer")
    command = ["cargo", "test", "--release", "--locked", "--manifest-path", root / "engine/Cargo.toml"]
    if target_directory is not None:
        command += ["--target-dir", target_directory]
    for package in packages:
        command += ["-p", package]
    # Match the worker features explicitly without selecting target-inactive
    # workspace packages as standalone binaries on Windows and macOS.
    command += ["--no-default-features", "--features",
                "ccp_bridge/ort-plane,ccp_bridge/wgpu-lut,ccp_bridge/rust-inference"]
    return command


def rust_build_outputs(work: Path, rid: str) -> dict[str, Path]:
    """Paths shared by Cargo invocation, packaging and component examples."""
    target = work / ("source-build/.build/rust-target" if rid == "osx-arm64" else "rust-target")
    if os.path.lexists(target):
        raise RuntimeError(f"Refusing pre-existing Rust build directory: {target}. Choose a new --output directory.")
    suffix = ".exe" if TARGETS[rid][0] == "Windows" else ""
    return {"target": target, "worker": target / f"release/ccp-engine{suffix}",
            "example": target / f"release/examples/wgpu_lut_check{suffix}"}


def emitted_package_versions(package: Path, payload: Path, rid: str, version: str) -> dict:
    """Check emitted OS metadata rather than inferring versions from filenames."""
    if rid == "osx-arm64":
        with (payload.parent / "Info.plist").open("rb") as stream:
            metadata = plistlib.load(stream)
        for key in ("CFBundleShortVersionString", "CFBundleVersion"):
            if metadata.get(key) != version:
                raise RuntimeError(f"macOS {key} does not match native version {version}")
        if metadata.get("LSMinimumSystemVersion") != "14.0":
            raise RuntimeError("macOS minimum version must match the checked runtimes")
        return {"application_bundle": metadata}
    if rid.startswith("win-"):
        result = {}
        installer = next(package.glob("*-Setup.exe"))
        for name, path in (("application", payload / "app/ColorCorrect.App.dll"), ("installer", installer)):
            quoted = str(path).replace("'", "''")
            command = (f"(Get-Item -LiteralPath '{quoted}').VersionInfo | "
                       "Select-Object FileVersion,ProductVersion | ConvertTo-Json -Compress")
            process = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command],
                                     check=True, capture_output=True, text=True)
            metadata = json.loads(process.stdout)
            for field in ("FileVersion", "ProductVersion"):
                actual = str(metadata.get(field, "")).split("+", 1)[0].strip()
                if actual not in (version, version + ".0"):
                    raise RuntimeError(f"{name} {field} {actual!r} does not match native version {version}")
            result[name] = metadata
        return result
    archive = next(package.glob("*.deb"))
    actual = subprocess.check_output(["dpkg-deb", "--field", str(archive), "Version"], text=True).strip()
    if actual != version:
        raise RuntimeError(f"Debian package version {actual!r} does not match native version {version}")
    return {"debian": actual}


def verify_macos_candidate(package: Path, work: Path, version: str, run) -> tuple[Path, dict]:
    """Check both distributions, then run from a relocated, space-containing path."""
    manifest = json.loads((package / "package-manifest.json").read_text())
    if (manifest.get("rid") != "osx-arm64" or manifest.get("version") != version
            or manifest.get("distribution_policy") != "application"
            or manifest.get("payload_root") != "ColorCorrect Studio.app/Contents/MacOS"):
        raise RuntimeError("macOS binary manifest has an incompatible delivery contract")
    receipts = []
    for kind in ("zip", "tar.gz"):
        destination = work / ("Relocated Applications" if kind == "zip" else "Verified Tar Applications")
        destination.mkdir(exist_ok=False)
        archive = package / f"ColorCorrect-Studio-{version}-osx-arm64.{kind}"
        if kind == "zip":
            with zipfile.ZipFile(archive) as source:
                for entry in source.infolist():
                    path = PurePosixPath(entry.filename)
                    if (path.is_absolute() or ".." in path.parts or "\\" in entry.filename
                            or not path.parts or path.parts[0] != "ColorCorrect Studio.app"
                            or (entry.external_attr >> 16) & 0o170000 == 0o120000):
                        raise RuntimeError("Unsafe macOS ZIP member")
            run(["ditto", "-x", "-k", archive, destination])
        else:
            with tarfile.open(archive) as source:
                for entry in source.getmembers():
                    path = PurePosixPath(entry.name)
                    if (path.is_absolute() or ".." in path.parts or "\\" in entry.name
                            or not path.parts or path.parts[0] != "ColorCorrect Studio.app"
                            or not (entry.isfile() or entry.isdir())):
                        raise RuntimeError("Unsafe macOS tar member")
                source.extractall(destination, filter="data")
        actual = {path.relative_to(destination).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
                  for path in destination.rglob("*") if path.is_file()}
        if actual != manifest.get("sha256"):
            raise RuntimeError(f"Relocated {kind} bundle differs from the signed payload manifest")
        bundle = destination / "ColorCorrect Studio.app"
        run(["codesign", "--verify", "--deep", "--strict", "--verbose=2", bundle])
        receipts.append({"archive": archive.name, "verified_files": len(actual), "codesign": "ad-hoc integrity verified"})
    return work / "Relocated Applications" / manifest["payload_root"], {"archives": receipts,
        "status": "both archives extracted, payload hashes and ad-hoc signatures verified",
        "signing": "ad-hoc only; not Developer ID signed or notarized"}


def verify_macos_runtime(payload: Path, work: Path, evidence: Path, env: dict) -> dict:
    """Execute shipped native helpers from the relocated bundle, without Python."""
    outputs = {}
    for name, command in (
        ("package-location", [payload / "ColorCorrect-Studio", "--package-check"]),
        ("cpu-setup", [payload / "setup", "--diagnose", "--verify", "--install", "--mode", "cpu"]),
    ):
        isolated = dict(env, XDG_CONFIG_HOME=str(work / "macos-setup-preferences"))
        result = subprocess.run(list(map(str, command)), cwd=work, env=isolated,
                                check=True, capture_output=True, text=True, timeout=180)
        (evidence / f"macos-{name}.stdout.json").write_text(result.stdout, encoding="utf-8")
        (evidence / f"macos-{name}.stderr.txt").write_text(result.stderr, encoding="utf-8")
        outputs[name] = json.loads(result.stdout)
    location = outputs["package-location"]
    if Path(location.get("root", "")).resolve() != payload.resolve() or location.get("architecture") != "arm64":
        raise RuntimeError("Relocated native macOS launcher selected the wrong private root")
    probes = outputs["cpu-setup"].get("probes", [])
    if len(probes) != 1 or probes[0].get("policy") != "cpu-inference" or probes[0].get("status") != "verified" or probes[0].get("exit_code") != 0:
        raise RuntimeError("Bundled macOS setup helper did not verify complete CPU operation")
    preferences = json.loads((work / "macos-setup-preferences/colorcorrect/setup-state.json").read_text())
    if (preferences.get("mode") != "cpu" or preferences.get("components") != ["cpu"]
            or preferences.get("detection_policy") != "cpu-inference" or preferences.get("correction_policy") != "cpu"):
        raise RuntimeError("Bundled macOS CPU-only setup did not save CPU inference and correction")
    return {"native_launcher": location, "cpu_probe": outputs["cpu-setup"], "preferences": preferences, "status": "passed"}


def clean_environment(package: Path, rid: str, runtime: Path | None = None) -> dict[str, str]:
    prefixes = ("CCP_", "ORT_", "DYLD_", "WGPU_", "VK_", "CUBECL_", "D3D12_", "MESA_", "DOTNET_", "COMPLUS_")
    remove = {"COLORCORRECT_EXE", "PYTHONPATH", "PYTHONHOME", "LD_LIBRARY_PATH", "LD_PRELOAD", "LD_AUDIT", "LIBTORCH", "LIBTORCH_USE_PYTORCH", "PATH", "PSMODULEPATH"}
    env = {key: value for key, value in os.environ.items() if key.upper() not in remove and not key.upper().startswith(prefixes)}
    system = TARGETS[rid][0]
    runtime = runtime or package
    engine = package / ("ccp-engine.exe" if system == "Windows" else "ccp-engine-launch")
    if system == "Darwin":
        engine = package / "ccp-engine"
    env.update(CCP_NEW_UI_ROOT=str(package), CCP_ENGINE_EXE=str(engine),
               ORT_DYLIB_PATH=str(runtime / {"Windows":"onnxruntime.dll", "Linux":"libonnxruntime.so", "Darwin":"libonnxruntime.dylib"}[system]),
               PYTHONUTF8="1", PYTHONNOUSERSITE="1", CCP_WSL_GPU_ADDON="0")
    if system == "Windows":
        windows_root = next((value for key, value in env.items() if key.upper() == "SYSTEMROOT"), None)
        if not windows_root:
            raise RuntimeError("Windows SYSTEMROOT is missing from the operating-system environment")
        windows = Path(windows_root)
        env["PATH"] = os.pathsep.join(map(str, (package, windows / "System32", windows, windows / "System32/WindowsPowerShell/v1.0")))
        env["PSModulePath"] = str(windows / "System32/WindowsPowerShell/v1.0/Modules")
    else:
        env["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin"
        env["DYLD_LIBRARY_PATH" if system == "Darwin" else "LD_LIBRARY_PATH"] = str(runtime)
    return env


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=TARGETS)
    parser.add_argument("--mode", choices=("build", "hardware"), default="build")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--inno-compiler", type=Path)
    parser.add_argument("--install-package", action="store_true", help="explicitly install the candidate on this disposable native test host")
    parser.add_argument("--macos-binary", action="store_true", help="explicitly package and validate relocated Apple-silicon application archives")
    args = parser.parse_args()
    if args.macos_binary and args.rid != "osx-arm64":
        parser.error("--macos-binary is available only for osx-arm64")
    # The read-only token belongs only to notice collection in the packager.
    # Do not pass it to installed applications, workers or acceptance harnesses.
    notice_token = os.environ.pop("CCP_NOTICE_GITHUB_TOKEN", None)
    if args.mode == "hardware" and os.environ.get("CCP_GPU_ACCEPTANCE_ENABLED") != "1":
        parser.error("Hardware acceptance requires an explicitly provisioned runner and CCP_GPU_ACCEPTANCE_ENABLED=1")
    root = Path(__file__).resolve().parents[1]
    work = (args.output or root / "artifacts/native-ci" / args.rid).resolve()
    evidence = work / "evidence"
    evidence.mkdir(parents=True, exist_ok=False)
    report = {"rid": args.rid, "platform": platform.platform(), "machine": platform.machine(), "mode": args.mode,
        "version": native_version(root), "source": source_identity(root),
        "status": "running", "native_ui": "pending interactive clipboard/high-DPI/workflow acceptance",
        "hardware_gpu": "pending; no hardware inferred from hosted runner label", "release_supported": False,
        "delivery": ("ad-hoc signed application candidate; not notarized" if args.macos_binary else
                     "source only; no binary artifact") if args.rid == "osx-arm64" else "unsigned release candidate"}
    report["free_disk_bytes_start"] = shutil.disk_usage(work).free

    def run(command, **kwargs):
        arguments = [str(x) for x in command]
        print("Running: " + repr(arguments), flush=True)
        with (evidence / "commands.log").open("a", encoding="utf-8") as log:
            log.write(repr(arguments) + "\n"); log.flush()
            try:
                subprocess.run(arguments, check=True, stdout=log, stderr=subprocess.STDOUT, **kwargs)
            except subprocess.CalledProcessError:
                log.flush()
                with (evidence / "commands.log").open("rb") as diagnostic:
                    diagnostic.seek(max(0, diagnostic.seek(0, os.SEEK_END) - 16384))
                    print(diagnostic.read().decode("utf-8", errors="replace"), flush=True)
                raise

    try:
        if (platform.system(), native_architecture()) != TARGETS[args.rid][:2]:
            raise RuntimeError("Runner/native architecture mismatch; emulation does not pass this gate")
        if TARGETS[args.rid][:2] not in executable_architectures(Path(sys.executable).resolve()):
            raise RuntimeError("Python interpreter is not native to this architecture")
        run([sys.executable, "-m", "unittest", "discover", "-s", root / "scripts/tests", "-p", "test_*.py"])
        run([sys.executable, root / "scripts/export_source.py", "--check"])
        report["source_contracts"] = "packaging/version/workflow contracts and current-source inventory passed"
        fixtures = work / "fixtures"
        shutil.copytree(root / "testdata/acceptance", fixtures)
        for name, expected in json.loads((fixtures / "manifest.json").read_text()).items():
            if hashlib.sha256((fixtures / name).read_bytes()).hexdigest() != expected:
                raise RuntimeError("Fixture checksum mismatch: " + name)
        exe = ".exe" if os.name == "nt" else ""
        # A fresh per-run location prevents ambient CARGO_TARGET_DIR or an old,
        # protected workspace executable from supplying a stale worker.
        rust_outputs = rust_build_outputs(work, args.rid)
        rust_target = rust_outputs["target"]
        if args.rid == "osx-arm64":
            run(["bash", root / "scripts/build_macos.sh", work / "source-build"])
            payload = work / "source-build"
            runtime = payload
            app = work / "source-build/app/ColorCorrect.App"
            if args.macos_binary:
                run([sys.executable, "-m", "pip", "wheel", "--no-deps", "--no-build-isolation",
                     "--wheel-dir", work / "wheels", root / "api"])
                wheel = next((work / "wheels").glob("*.whl"))
                package_environment = dict(os.environ)
                if notice_token:
                    package_environment["CCP_NOTICE_GITHUB_TOKEN"] = notice_token
                run(["bash", root / "scripts/package_macos.sh", "--binary", "--source-build", payload,
                     "--output", work / "package", "--wheel", wheel, "--fetch-notices"], env=package_environment)
                payload, report["macos_distribution"] = verify_macos_candidate(work / "package", work, report["version"], run)
                runtime = payload
                with (payload.parent / "Info.plist").open("rb") as stream:
                    app = payload / plistlib.load(stream)["CFBundleExecutable"]
                report["emitted_package_versions"] = emitted_package_versions(work / "package", payload, args.rid, report["version"])
                shutil.copy2(work / "package/package-manifest.json", evidence / "package-manifest.json")
        else:
            runtime = work / "runtime"
            report["runtime"] = fetch(args.rid, runtime, work / "download-cache")
            run(["cargo", "build", "--locked", "--manifest-path", root / "engine/Cargo.toml", "--target-dir", rust_target, "--release", "-j", "2",
                 "-p", "ccp_bridge", "-p", "ccp_detect", "--no-default-features", "--features", "ort-plane,wgpu-lut,rust-inference",
                 "--bin", "ccp-engine", "--example", "wgpu_lut_check"])
            command = [sys.executable, root / "scripts/package_native.py", "--rid", args.rid,
                "--worker", rust_outputs["worker"], "--ort-runtime", runtime,
                "--output", work / "package", "--fetch-notices"]
            if os.name == "nt":
                command += ["--vc-redist", fetch_vc_redist(args.rid, work / "prerequisites")]
            else:
                command += ["--deb"]
            package_environment = dict(os.environ)
            if notice_token:
                package_environment["CCP_NOTICE_GITHUB_TOKEN"] = notice_token
            run(command, env=package_environment)
            if os.name == "nt":
                compiler = args.inno_compiler or Path(os.environ.get("CCP_INNO_COMPILER", "C:/Program Files (x86)/Inno Setup 6/ISCC.exe"))
                if not compiler.is_file():
                    raise RuntimeError("Inno compiler is missing; supply a pinned reviewed compiler with --inno-compiler")
                run([compiler, f"/DDist={work / 'package/payload'}", f"/O{work / 'package'}", root / "installer/app.iss"])
                from package_support import digest
                package_files = sorted(path for path in (work / "package").iterdir() if path.suffix in (".exe", ".zip"))
                (work / "package/SHA256SUMS").write_text("".join(f"{digest(path)}  {path.name}\n" for path in package_files), encoding="ascii")
            payload = work / "extracted"
            archive = next((work / "package").glob("*.zip" if os.name == "nt" else "*.tar.gz"))
            shutil.unpack_archive(archive, payload)
            manifest = json.loads((work / "package/package-manifest.json").read_text())
            if manifest["version"] != report["version"]:
                raise RuntimeError("Native package manifest version differs from source")
            report["emitted_package_versions"] = emitted_package_versions(work / "package", payload, args.rid, report["version"])
            for name, expected in manifest["sha256"].items():
                if hashlib.sha256((payload / name).read_bytes()).hexdigest() != expected:
                    raise RuntimeError("Extracted package checksum mismatch: " + name)
            shutil.copy2(work / "package/package-manifest.json", evidence / "package-manifest.json")
            report["portable_archive"] = "extracted and all payload hashes verified"
            if args.install_package:
                if os.name == "nt":
                    installed = work / "installed"
                    installer = next((work / "package").glob("*-Setup.exe"))
                    run([installer, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/CURRENTUSER", "/NORESTART",
                         "/COMPONENTS=app,python", "/TASKS=", f"/DIR={installed}", f"/LOG={evidence / 'installer.log'}"])
                else:
                    installer = next((work / "package").glob("*.deb"))
                    run(["sudo", "apt-get", "install", "-y", installer])
                    installed = Path("/opt/colorcorrect-studio")
                components = json.loads((installed / "setup-components.json").read_text(encoding="utf-8"))
                optional_paths = {item["path"] for component in components["components"]
                                  if not component["required"] for item in component["files"]}
                required_paths = {item["path"] for component in components["components"]
                                  if component["required"] for item in component["files"]}
                optional_paths -= required_paths
                omitted = []
                for name, expected in manifest["sha256"].items():
                    if name in optional_paths and not (installed / name).exists():
                        omitted.append(name)
                        continue
                    if hashlib.sha256((installed / name).read_bytes()).hexdigest() != expected:
                        raise RuntimeError("Installed package checksum mismatch: " + name)
                payload = installed
                report["native_installation"] = {"status": "installed file hashes verified", "omitted_optional_files": omitted}
            else:
                report["native_installation"] = "pending; only portable archive extraction requested"
            app = payload / f"app/ColorCorrect.App{exe}"
            runtime = payload
        # Component tests are distinct from interactive UI/hardware acceptance.
        report["component_tests"] = {}
        component_failures = []
        report["rust_target_directory"] = str(rust_target)
        rust_tests = rust_component_test_command(root, args.rid, rust_target)
        report["rust_component_packages"] = [rust_tests[index + 1] for index, value in enumerate(rust_tests) if value == "-p"]
        component_commands = {
            "rust_workspace": rust_tests,
            "dotnet_engine": ["dotnet", "test", root / "app/ColorCorrect.Engine.Tests/ColorCorrect.Engine.Tests.csproj", "-c", "Release",
                "-p:RestoreLockedMode=true", "--logger", "trx;LogFileName=engine.trx", "--results-directory", evidence],
            "dotnet_headless": ["dotnet", "test", root / "app/ColorCorrect.App.Headless.Tests/ColorCorrect.App.Headless.Tests.csproj", "-c", "Release",
                "-p:RestoreLockedMode=true", "--logger", "trx;LogFileName=headless.trx", "--results-directory", evidence],
        }
        for name, command in component_commands.items():
            try:
                run(command)
                report["component_tests"][name] = "passed; inspect logs/TRX for explicit skips"
            except subprocess.CalledProcessError as error:
                report["component_tests"][name] = "failed"
                component_failures.append(f"{name}: {error.returncode}")
        report["visual_baselines"] = "pending native screenshot/clipboard/high-DPI baselines; headless structural checks do not certify rendering"
        run([sys.executable, "-m", "venv", work / "venv"])
        python = work / ("venv/Scripts/python.exe" if os.name == "nt" else "venv/bin/python")
        if args.rid == "osx-arm64" and not args.macos_binary:
            run([sys.executable, "-m", "pip", "wheel", "--no-deps", "--no-build-isolation", "--wheel-dir", work / "wheels", root / "api"])
            wheel = next((work / "wheels").glob("*.whl"))
        else:
            wheel = next((payload / "python").glob("*.whl"))
        run([python, "-m", "pip", "install", "--no-index", "--no-deps", wheel])
        env = clean_environment(payload, args.rid, runtime)
        if args.macos_binary:
            report["macos_native_helpers"] = verify_macos_runtime(payload, work, evidence, env)
        try:
            run([python, "-I", "-m", "unittest", "discover", "-s", root / "api/tests", "-p", "test*.py"], cwd=work, env=env)
            report["component_tests"]["python_api"] = "source API unit tests passed; installed-wheel behavior checked separately by isolated smoke"
        except subprocess.CalledProcessError as error:
            report["component_tests"]["python_api"] = "failed"
            component_failures.append(f"python_api: {error.returncode}")
        if args.rid != "osx-arm64":
            if os.name == "nt":
                run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", payload / "check-system.ps1"],
                    cwd=work, env=env)
            else:
                run([payload / "check-system"], cwd=work, env=env)
            # A deliberately nonexistent adapter proves the production LUT path
            # can recover to CPU on ordinary runners without advertising GPU use.
            fallback_environment = dict(env, CCP_LUT_ADAPTER="__ccp_intentionally_missing_adapter__")
            run([rust_outputs["example"], "1048833", "65", "1"],
                cwd=work, env=fallback_environment)
        # The smoke script is copied outside the source tree and imports only the installed SDK.
        smoke = work / "acceptance_bootstrap.py"
        shutil.copy2(root / "scripts/acceptance_bootstrap.py", smoke)
        run([python, "-I", smoke, "--package", payload, "--fixtures", fixtures, "--output", evidence / "sdk-smoke"], cwd=work, env=env)
        report["installed_sdk_smoke"] = "passed" if args.rid != "osx-arm64" or args.macos_binary else "installed SDK against local source build passed"
        # Full CPU workflows run on every native architecture, independently of
        # whether hosted graphics happen to be available. Auto must also work.
        cpu_workflow = work / "acceptance_cpu_workflow.py"
        shutil.copy2(root / "scripts/acceptance_cpu_workflow.py", cpu_workflow)
        for policy in ("cpu-inference", "auto"):
            run([python, "-I", cpu_workflow, "--package", payload, "--fixtures", fixtures,
                 "--output", evidence / ("sdk-full-" + policy), "--backend", policy], cwd=work, env=env)
        report["installed_full_workflows"] = "explicit CPU inference and Auto completed detection/FFC/fit/apply/batch/reload"
        if args.mode == "hardware":
            test = work / "acceptance_installed.py"
            shutil.copy2(root / "api/tests/acceptance_installed.py", test)
            run([python, "-I", test, "--package", payload, "--fixtures", fixtures, "--output", evidence / "sdk-full"], cwd=work, env=env)
            report["hardware_gpu"] = "workflow completed; inspect actual provider evidence before certification"
        with (evidence / "app.stdout.txt").open("w") as out, (evidence / "app.stderr.txt").open("w") as err:
            proc = subprocess.Popen([str(app)], cwd=work, env=env, stdout=out, stderr=err)
            try:
                time.sleep(10)
                if proc.poll() is not None:
                    raise RuntimeError(f"Native app exited during launch: {proc.returncode}")
                report["native_app_launch"] = "passed; ten-second liveness only"
            finally:
                if proc.poll() is None:
                    proc.terminate(); proc.wait(timeout=30)
        if component_failures:
            raise RuntimeError("Component checks failed: " + "; ".join(component_failures))
        report["status"] = "build, component tests and smoke passed; release gates remain pending"
    except Exception as error:
        report.update(status="failed", error=repr(error))
        raise
    finally:
        report["free_disk_bytes_end"] = shutil.disk_usage(work).free
        (evidence / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
