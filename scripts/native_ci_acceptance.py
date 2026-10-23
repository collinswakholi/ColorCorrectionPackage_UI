"""Native build/installed smoke; hardware GPU acceptance is a separate opt-in gate.

macOS only builds source locally. It never calls the packager or exports binaries.
No signing, release creation, publishing or driver installation is performed.
"""
from pathlib import Path
import argparse
import hashlib
import json
import os
import platform
import shutil
import subprocess
import sys
import tempfile
import time
from package_native import native_architecture, executable_architectures, TARGETS
from fetch_gpu_runtime import fetch, fetch_vc_redist
from package_support import native_version, source_identity


def emitted_package_versions(package: Path, payload: Path, rid: str, version: str) -> dict:
    """Check emitted OS metadata rather than inferring versions from filenames."""
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
    args = parser.parse_args()
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
        "delivery": "source only; no binary artifact" if args.rid == "osx-arm64" else "unsigned release candidate"}
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
        if args.rid == "osx-arm64":
            run(["bash", root / "scripts/build_macos.sh", work / "source-build"])
            payload = work / "source-build"
            runtime = payload
            app = work / "source-build/app/ColorCorrect.App"
        else:
            runtime = work / "runtime"
            report["runtime"] = fetch(args.rid, runtime, work / "download-cache")
            run(["cargo", "build", "--locked", "--manifest-path", root / "engine/Cargo.toml", "--release", "-j", "2",
                 "-p", "ccp_bridge", "-p", "ccp_detect", "--no-default-features", "--features", "ort-plane,wgpu-lut,rust-inference",
                 "--bin", "ccp-engine", "--example", "wgpu_lut_check"])
            command = [sys.executable, root / "scripts/package_native.py", "--rid", args.rid,
                "--worker", root / f"engine/target/release/ccp-engine{exe}", "--ort-runtime", runtime,
                "--output", work / "package", "--fetch-notices"]
            if os.name == "nt":
                command += ["--vc-redist", fetch_vc_redist(args.rid, work / "prerequisites")]
            else:
                command += ["--deb"]
            run(command)
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
                for name, expected in manifest["sha256"].items():
                    if hashlib.sha256((installed / name).read_bytes()).hexdigest() != expected:
                        raise RuntimeError("Installed package checksum mismatch: " + name)
                payload = installed
                report["native_installation"] = "installer completed; installed file hashes verified"
            else:
                report["native_installation"] = "pending; only portable archive extraction requested"
            app = payload / f"app/ColorCorrect.App{exe}"
            runtime = payload
        # Component tests are distinct from interactive UI/hardware acceptance.
        report["component_tests"] = {}
        component_failures = []
        component_commands = {
            "rust_workspace": ["cargo", "test", "--release", "--locked", "--manifest-path", root / "engine/Cargo.toml", "--workspace"],
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
        if args.rid == "osx-arm64":
            run([sys.executable, "-m", "pip", "wheel", "--no-deps", "--no-build-isolation", "--wheel-dir", work / "wheels", root / "api"])
            wheel = next((work / "wheels").glob("*.whl"))
        else:
            wheel = next((payload / "python").glob("*.whl"))
        run([python, "-m", "pip", "install", "--no-index", "--no-deps", wheel])
        env = clean_environment(payload, args.rid, runtime)
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
            run([root / f"engine/target/release/examples/wgpu_lut_check{exe}", "1048833", "65", "1"],
                cwd=work, env=fallback_environment)
        # The smoke script is copied outside the source tree and imports only the installed SDK.
        smoke = work / "acceptance_bootstrap.py"
        shutil.copy2(root / "scripts/acceptance_bootstrap.py", smoke)
        run([python, "-I", smoke, "--package", payload, "--fixtures", fixtures, "--output", evidence / "sdk-smoke"], cwd=work, env=env)
        report["installed_sdk_smoke"] = "passed" if args.rid != "osx-arm64" else "installed SDK against local source build passed"
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
