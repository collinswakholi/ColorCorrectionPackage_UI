"""Explicitly package a checked local Apple-silicon build; never publishes it.

The default source-build workflow remains independent. Distribution requires
--binary, native arm64 macOS 14+, complete notices, and checked private inputs.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import platform
import plistlib
import re
import shutil
import stat
import struct
import subprocess
import sys
import tarfile
import zipfile

from package_native import executable_architectures
from package_support import (DOCS, MODELS, REFERENCES, REQUIRED_NOTICES,
    check_model_manifest, check_runtime_manifest, digest, native_version, source_identity)
from collect_dependency_notices import collect, validate_notices

RID = "osx-arm64"
APP_NAME = "ColorCorrect Studio.app"
EXECUTABLE = "ColorCorrect-Studio"
MACH_MAGICS = {b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf", b"\xca\xfe\xba\xbe",
               b"\xbe\xba\xfe\xca", b"\xca\xfe\xba\xbf", b"\xbf\xba\xfe\xca"}

# No Python, shell quoting, current directory or build-directory dependency at
# runtime. Foundation is an OS framework; NSTask probe output uses private files
# so a full stdout/stderr pipe cannot deadlock its bounded wait.
LAUNCHER = r'''
#import <Foundation/Foundation.h>
#include <mach-o/dyld.h>
#include <limits.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static BOOL jsonTrue(id value) { return value == (__bridge id)kCFBooleanTrue; }

static NSDictionary *probe(NSString *root, NSString *policy) {
    NSFileManager *fm = NSFileManager.defaultManager;
    NSString *tmp = [NSTemporaryDirectory() stringByAppendingPathComponent:[@"ccp-setup-" stringByAppendingString:NSUUID.UUID.UUIDString]];
    NSError *error = nil;
    if (![fm createDirectoryAtPath:tmp withIntermediateDirectories:NO attributes:@{NSFilePosixPermissions:@0700} error:&error])
        return @{ @"policy":policy, @"status":@"failed", @"reason":error.localizedDescription, @"result":@{} };
    NSMutableDictionary *record = [@{ @"policy":policy, @"status":@"failed", @"result":@{} } mutableCopy];
    @try {
        NSString *outPath = [tmp stringByAppendingPathComponent:@"stdout"];
        NSString *errPath = [tmp stringByAppendingPathComponent:@"stderr"];
        [fm createFileAtPath:outPath contents:nil attributes:@{NSFilePosixPermissions:@0600}];
        [fm createFileAtPath:errPath contents:nil attributes:@{NSFilePosixPermissions:@0600}];
        NSFileHandle *out = [NSFileHandle fileHandleForWritingAtPath:outPath];
        NSFileHandle *err = [NSFileHandle fileHandleForWritingAtPath:errPath];
        NSPipe *input = NSPipe.pipe;
        NSTask *task = NSTask.new;
        task.executableURL = [NSURL fileURLWithPath:[root stringByAppendingPathComponent:@"ccp-engine"]];
        task.currentDirectoryURL = [NSURL fileURLWithPath:tmp];
        NSMutableDictionary *env = [NSProcessInfo.processInfo.environment mutableCopy];
        env[@"CCP_NEW_UI_ROOT"] = root;
        env[@"CCP_ENGINE_EXE"] = [root stringByAppendingPathComponent:@"ccp-engine"];
        env[@"ORT_DYLIB_PATH"] = [root stringByAppendingPathComponent:@"libonnxruntime.dylib"];
        env[@"DYLD_LIBRARY_PATH"] = [[root stringByAppendingString:@":"] stringByAppendingString:[root stringByAppendingPathComponent:@"app"]];
        env[@"CCP_INFERENCE_COMPONENTS"] = @"cpu,webgpu";
        task.environment = env;
        task.standardInput = input; task.standardOutput = out; task.standardError = err;
        if (![task launchAndReturnError:&error]) { [out closeFile]; [err closeFile]; record[@"reason"] = error.localizedDescription; return record; }
        @try {
            NSDictionary *request = @{ @"cmd":@"setup_probe", @"policy":policy, @"force_cpu":@([policy isEqualToString:@"cpu-inference"]) };
            NSMutableData *data = [[NSJSONSerialization dataWithJSONObject:request options:0 error:nil] mutableCopy];
            [data appendData:[@"\n{\"cmd\":\"quit\"}\n" dataUsingEncoding:NSUTF8StringEncoding]];
            [input.fileHandleForWriting writeData:data]; [input.fileHandleForWriting closeFile];
            NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:120];
            while (task.running && deadline.timeIntervalSinceNow > 0) [NSThread sleepForTimeInterval:0.02];
            if (task.running) {
                record[@"reason"] = @"Engine setup probe exceeded 120 seconds.";
                [task terminate]; [NSThread sleepForTimeInterval:0.2];
                if (task.running) kill(task.processIdentifier, SIGKILL);
            }
            [task waitUntilExit];
            record[@"exit_code"] = @(task.terminationStatus);
        } @finally {
            if (task.running) { kill(task.processIdentifier, SIGKILL); [task waitUntilExit]; }
            [out closeFile]; [err closeFile];
        }
        NSDictionary *attrs = [fm attributesOfItemAtPath:outPath error:nil];
        if ([attrs[NSFileSize] unsignedLongLongValue] > 1048576) { record[@"reason"] = @"Setup probe produced too much output."; return record; }
        NSString *output = [NSString stringWithContentsOfFile:outPath encoding:NSUTF8StringEncoding error:nil] ?: @"";
        for (NSString *line in [output componentsSeparatedByCharactersInSet:NSCharacterSet.newlineCharacterSet]) {
            id reply = [NSJSONSerialization JSONObjectWithData:[line dataUsingEncoding:NSUTF8StringEncoding] options:0 error:nil];
            if ([reply isKindOfClass:NSDictionary.class] && reply[@"setup_verified"] != nil) record[@"result"] = reply;
        }
        NSDictionary *reply = record[@"result"];
        if ([record[@"exit_code"] intValue] == 0 && jsonTrue(reply[@"setup_verified"]) &&
            jsonTrue(reply[@"ok"]) && jsonTrue(reply[@"inference"][@"warmed"]) &&
            [reply[@"correction"][@"status"] isEqual:@"verified"] && record[@"reason"] == nil) record[@"status"] = @"verified";
        else if (record[@"reason"] == nil) {
            NSString *reason = reply[@"inference"][@"error"] ?: reply[@"error"];
            if (reason.length == 0) {
                NSFileHandle *reader=[NSFileHandle fileHandleForReadingAtPath:errPath];
                reason=[[NSString alloc] initWithData:[reader readDataOfLength:4096] encoding:NSUTF8StringEncoding]; [reader closeFile];
            }
            record[@"reason"] = reason.length ? [reason substringToIndex:MIN(reason.length, 4096)] : @"Engine probe failed or returned no verified result.";
        }
    } @catch (NSException *exception) { record[@"reason"] = exception.reason ?: @"Setup probe failed."; }
    @finally { [fm removeItemAtPath:tmp error:nil]; }
    return record;
}

int main(int argc, char **argv) { @autoreleasepool {
    char path[PATH_MAX]; uint32_t size = sizeof(path);
    if (_NSGetExecutablePath(path, &size) != 0) { fprintf(stderr, "Application path is too long.\n"); return 1; }
    char *absolute = realpath(path, NULL);
    if (!absolute) { perror("Application path"); return 1; }
    char *slash = strrchr(absolute, '/'); if (!slash) { free(absolute); return 1; }
    NSString *entry = @(slash + 1); *slash = 0;
    NSString *root = @(absolute); free(absolute);
    if ([entry isEqual:@"ColorCorrect-Studio"])
        root = [[root stringByAppendingPathComponent:@"../Resources/runtime"] stringByStandardizingPath];
    setenv("CCP_NEW_UI_ROOT", root.fileSystemRepresentation, 1);
    setenv("CCP_ENGINE_EXE", [root stringByAppendingPathComponent:@"ccp-engine"].fileSystemRepresentation, 1);
    setenv("ORT_DYLIB_PATH", [root stringByAppendingPathComponent:@"libonnxruntime.dylib"].fileSystemRepresentation, 1);
    setenv("DYLD_LIBRARY_PATH", [[root stringByAppendingString:@":"] stringByAppendingString:[root stringByAppendingPathComponent:@"app"]].fileSystemRepresentation, 1);
    setenv("DOTNET_ROOT", [root stringByAppendingPathComponent:@"app"].fileSystemRepresentation, 1);
    setenv("DOTNET_MULTILEVEL_LOOKUP", "0", 1);
    if ([entry isEqual:@"check-system"]) {
        for (NSString *name in @[@"ccp-engine", @"libonnxruntime.dylib", @"app/ColorCorrect.App"])
            if (![NSFileManager.defaultManager isReadableFileAtPath:[root stringByAppendingPathComponent:name]]) {
                fprintf(stderr,"Missing private application file: %s. Restore the original archive.\n",name.UTF8String); return 1;
            }
        NSTask *signature = NSTask.new;
        signature.executableURL = [NSURL fileURLWithPath:@"/usr/bin/codesign"];
        signature.arguments = @[@"--verify", @"--deep", @"--strict", [[root stringByAppendingPathComponent:@"../../.."] stringByStandardizingPath]];
        signature.standardOutput = NSFileHandle.fileHandleWithStandardError;
        signature.standardError = NSFileHandle.fileHandleWithStandardError;
        NSError *error=nil;
        if (![signature launchAndReturnError:&error]) { fprintf(stderr,"%s\n",error.localizedDescription.UTF8String); return 1; }
        NSDate *deadline=[NSDate dateWithTimeIntervalSinceNow:30];
        while (signature.running && deadline.timeIntervalSinceNow > 0) [NSThread sleepForTimeInterval:0.02];
        if (signature.running) { kill(signature.processIdentifier,SIGKILL); [signature waitUntilExit]; fprintf(stderr,"Signature verification timed out.\n"); return 1; }
        [signature waitUntilExit]; if (signature.terminationStatus != 0) return 1;
        if (argc > 1 && !strcmp(argv[1],"--verify")) {
            NSDictionary *result=probe(root,@"cpu-inference");
            NSData *data=[NSJSONSerialization dataWithJSONObject:@{@"schema":@1,@"probes":@[result]} options:NSJSONWritingPrettyPrinted error:nil];
            fwrite(data.bytes,1,data.length,stdout); puts(""); return [result[@"status"] isEqual:@"verified"] ? 0 : 1;
        }
        puts("Private application files and bundle signatures are present. Run ./setup --diagnose --verify for CPU/GPU checks."); return 0;
    }
    if (argc > 1 && strcmp(argv[1], "--package-check") == 0) {
        NSData *data = [NSJSONSerialization dataWithJSONObject:@{@"root":root, @"architecture":@"arm64", @"minimum_os":@"14.0"} options:0 error:nil];
        fwrite(data.bytes, 1, data.length, stdout); puts(""); return 0;
    }
    BOOL setupEntry=[entry isEqual:@"setup"];
    if (setupEntry || (argc > 1 && strcmp(argv[1], "--setup") == 0)) {
        BOOL verify = NO, install = NO; NSString *mode = @"recommended", *components = @"";
        for (int i=setupEntry?1:2; i<argc; i++) {
            if (!strcmp(argv[i], "--verify")) verify = YES;
            else if (!strcmp(argv[i], "--install")) install = YES;
            else if ((!strcmp(argv[i], "--mode") || !strcmp(argv[i], "--components")) && i+1<argc) {
                BOOL selection = !strcmp(argv[i], "--mode"); NSString *value = @(argv[++i]);
                if (selection) mode=value; else components=value;
            } else if (strcmp(argv[i], "--diagnose")) { fprintf(stderr,"Unknown setup option.\n"); return 64; }
        }
        if (![@[@"recommended",@"cpu",@"custom"] containsObject:mode]) { fprintf(stderr,"Invalid setup mode.\n"); return 64; }
        BOOL gpu = ![mode isEqual:@"cpu"] && (![mode isEqual:@"custom"] || [[components componentsSeparatedByString:@","] containsObject:@"webgpu"]);
        for (NSString *value in [components componentsSeparatedByString:@","])
            if (value.length && ![@[@"cpu",@"webgpu"] containsObject:value]) { fprintf(stderr,"Unknown component.\n"); return 64; }
        NSArray *selected = gpu ? @[@"cpu",@"webgpu"] : @[@"cpu"];
        if (install) {
            fprintf(stderr,"All required runtimes are bundled. Saving user selection: %s. No drivers or system packages are installed.\n", [[selected componentsJoinedByString:@","] UTF8String]);
            const char *config = getenv("XDG_CONFIG_HOME"), *home = getenv("HOME");
            NSString *folder = config ? @(config) : [(home ? @(home) : NSHomeDirectory()) stringByAppendingPathComponent:@".config"];
            folder = [folder stringByAppendingPathComponent:@"colorcorrect"];
            NSError *error=nil;
            if (![NSFileManager.defaultManager createDirectoryAtPath:folder withIntermediateDirectories:YES attributes:nil error:&error]) { fprintf(stderr,"%s\n",error.localizedDescription.UTF8String); return 1; }
            BOOL cpuOnly=[mode isEqual:@"cpu"];
            NSData *state = [NSJSONSerialization dataWithJSONObject:@{@"schema":@1,@"mode":mode,@"components":selected,
                @"detection_policy":cpuOnly?@"cpu-inference":@"auto", @"correction_policy":cpuOnly?@"cpu":@"auto"} options:NSJSONWritingPrettyPrinted error:nil];
            if (![state writeToFile:[folder stringByAppendingPathComponent:@"setup-state.json"] options:NSDataWritingAtomic error:&error]) { fprintf(stderr,"%s\n",error.localizedDescription.UTF8String); return 1; }
        }
        NSMutableArray *probes=NSMutableArray.new;
        if (verify) { [probes addObject:probe(root,@"cpu-inference")]; if (gpu) [probes addObject:probe(root,@"webgpu")]; }
        NSDictionary *report=@{@"schema":@1,@"rid":@"osx-arm64",@"minimum_os":@"14.0",@"inventory_status":verify?@"Runtime probes completed":@"Not yet checked",@"mode":mode,@"components":selected,@"probes":probes,@"system_changes":@"None; private runtimes are bundled. Drivers are managed by macOS."};
        NSData *data=[NSJSONSerialization dataWithJSONObject:report options:NSJSONWritingPrettyPrinted error:nil]; fwrite(data.bytes,1,data.length,stdout); puts(""); return 0;
    }
    NSString *app = [root stringByAppendingPathComponent:@"app/ColorCorrect.App"];
    argv[0]=(char *)app.fileSystemRepresentation;
    execv(argv[0], argv); perror("Could not launch ColorCorrect Studio"); return 1;
} }
'''


def run(command, **kwargs):
    return subprocess.run([str(value) for value in command], check=True, **kwargs)


def native_files(folder: Path) -> list[Path]:
    result = []
    for path in sorted(folder.rglob("*")):
        if path.is_file():
            with path.open("rb") as stream:
                if stream.read(4) in MACH_MAGICS:
                    result.append(path)
    return result


def safe_copy(source: Path, destination: Path) -> None:
    """Materialize internal file links; reject directory links and escaped input."""
    boundary = source.resolve(strict=True)
    for path in source.rglob("*"):
        if path.is_symlink() and (path.is_dir() or not path.resolve(strict=True).is_relative_to(boundary)):
            raise ValueError("Package input contains an unsafe link: " + str(path))
    shutil.copytree(source, destination, ignore=shutil.ignore_patterns("*.pdb", "*.PDB", "__pycache__", ".DS_Store"))


def system_library(name: str) -> bool:
    return name.startswith(("/usr/lib/", "/System/Library/")) and ".." not in Path(name).parts


def dependencies(path: Path) -> list[str]:
    lines = run(["otool", "-L", path], capture_output=True, text=True).stdout.splitlines()[1:]
    return [line.strip().split(" (", 1)[0] for line in lines if line.strip()]


def rpaths(path: Path) -> list[str]:
    text = run(["otool", "-l", path], capture_output=True, text=True).stdout
    return re.findall(r"cmd LC_RPATH\s+cmdsize \d+\s+path (.*?) \(offset", text)


def resolve_private(name: str, owner: Path, folder: Path, by_name: dict[str, list[Path]]) -> Path:
    if name.startswith("@loader_path/"):
        candidate = owner.parent / name.removeprefix("@loader_path/")
    elif name.startswith("@executable_path/"):
        candidate = folder / name.removeprefix("@executable_path/")
    else:
        candidate = Path(name)
    if candidate.is_absolute() and candidate.is_file() and candidate.resolve().is_relative_to(folder):
        return candidate.resolve()
    options = by_name.get(Path(name).name, [])
    if len(options) != 1:
        raise ValueError(f"Unresolved or ambiguous non-system dependency {name!r}: {owner}")
    return options[0]


def relocate_native(folder: Path) -> dict:
    files = native_files(folder)
    by_name: dict[str, list[Path]] = {}
    for path in files:
        archs = executable_architectures(path)
        if ("Darwin", "arm64") not in archs:
            raise ValueError("Non-Apple-silicon native payload: " + str(path))
        if archs != {("Darwin", "arm64")}:
            temporary = path.with_name(path.name + ".arm64")
            if os.path.lexists(temporary): raise FileExistsError(temporary)
            run(["lipo", path, "-thin", "arm64", "-output", temporary]); temporary.replace(path)
        ids = run(["otool", "-D", path], capture_output=True, text=True).stdout.splitlines()[1:]
        aliases = {path.name, *(Path(value.strip()).name for value in ids if value.strip())}
        for alias in aliases: by_name.setdefault(alias, []).append(path)
    report = {"native_files": [], "missing": [], "status": "arm64 private dependency closure checked"}
    for path in files:
        commands = run(["otool", "-l", path], capture_output=True, text=True).stdout
        minimums = re.findall(r"cmd LC_BUILD_VERSION\b(?:(?!Load command).)*?\bminos (\d+\.\d+(?:\.\d+)?)", commands, re.S)
        minimums += re.findall(r"cmd LC_VERSION_MIN_MACOSX\b(?:(?!Load command).)*?\bversion (\d+\.\d+(?:\.\d+)?)", commands, re.S)
        if any(tuple(map(int, value.split("."))) > (14, 0, 0) for value in minimums):
            raise ValueError("Native dependency requires newer than macOS 14: " + str(path))
        # The dylib ID appears in otool -L too, but is not an imported dependency.
        ids = run(["otool", "-D", path], capture_output=True, text=True).stdout.splitlines()[1:]
        own_id = ids[0].strip() if ids else None
        resolved = []
        for name in dependencies(path):
            if name == own_id:
                continue
            if system_library(name):
                resolved.append({"name": name, "kind": "macOS system"}); continue
            target = resolve_private(name, path, folder, by_name)
            new = "@loader_path/" + Path(os.path.relpath(target, path.parent)).as_posix()
            if name != new:
                run(["install_name_tool", "-change", name, new, path])
            resolved.append({"name": new, "resolved": target.relative_to(folder).as_posix()})
        if own_id:
            run(["install_name_tool", "-id", "@rpath/" + path.name, path])
        for value in rpaths(path):
            if value.startswith("@loader_path"):
                safe = (path.parent / value.removeprefix("@loader_path").lstrip("/")).resolve().is_relative_to(folder)
            elif value.startswith("@executable_path"):
                safe = (folder / value.removeprefix("@executable_path").lstrip("/")).resolve().is_relative_to(folder)
            else: safe = system_library(value + "/")
            if not safe:
                run(["install_name_tool", "-delete_rpath", value, path])
        report["native_files"].append({"path": path.relative_to(folder).as_posix(), "dependencies": resolved})
    # Re-read final load commands: existence by basename is not evidence that
    # dyld can reach it from an arbitrary extracted/relocated application.
    for path in files:
        ids = run(["otool", "-D", path], capture_output=True, text=True).stdout.splitlines()[1:]
        own_id = ids[0].strip() if ids else None
        for name in dependencies(path):
            if name == own_id or system_library(name): continue
            if not name.startswith("@loader_path/"):
                raise ValueError("Non-relocatable load command: " + name)
            target = (path.parent / name.removeprefix("@loader_path/")).resolve(strict=True)
            if not target.is_relative_to(folder): raise ValueError("Dependency escaped application: " + name)
    return report


def archive_zip(payload: Path, destination: Path) -> None:
    with zipfile.ZipFile(destination, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(payload.rglob("*")):
            if path.is_symlink(): raise ValueError("Distribution links are not allowed")
            if path.is_file():
                info = zipfile.ZipInfo.from_file(path, path.relative_to(payload).as_posix())
                info.create_system = 3
                info.external_attr = (stat.S_IFREG | stat.S_IMODE(path.stat().st_mode)) << 16
                info.compress_type = zipfile.ZIP_DEFLATED
                with path.open("rb") as source, archive.open(info, "w", force_zip64=True) as output:
                    shutil.copyfileobj(source, output)


def build(root: Path, source: Path, output: Path, wheel: Path, notices: Path | None, fetch_notices: bool) -> dict:
    root, source, wheel = root.resolve(strict=True), source.resolve(strict=True), wheel.resolve(strict=True)
    if platform.system() != "Darwin" or platform.machine() != "arm64":
        raise ValueError("Package natively on Apple-silicon macOS; Intel and Rosetta builds are excluded")
    version_os = run(["sw_vers", "-productVersion"], capture_output=True, text=True).stdout.strip()
    if int(version_os.split(".")[0]) < 14: raise ValueError("macOS 14 or later is required")
    version = native_version(root)
    runtime = check_runtime_manifest(source, RID, "Darwin")
    if check_model_manifest(source) != check_model_manifest(root): raise ValueError("Source-build models do not match source")
    if any("torch" in path.name.lower() or path.name.lower().startswith("libc10") for path in source.glob("*.dylib")):
        raise ValueError("Removed ML runtimes cannot be distributed")
    if wheel.suffix != ".whl": raise ValueError("Supply the built SDK wheel")
    output = output.resolve()
    if output.is_relative_to(source) or source.is_relative_to(output) or any(output.is_relative_to(root / name) for name in ("models", "refs", "api", "docs", "app")):
        raise ValueError("Package output must be outside copied source-build/resources")
    if os.path.lexists(output): raise FileExistsError(output)
    output.mkdir(parents=True)
    payload = output / "payload"; bundle = payload / APP_NAME; contents = bundle / "Contents"
    macos = contents / "MacOS"; macos.mkdir(parents=True)
    resources = contents / "Resources"; resources.mkdir()
    folder = resources / "runtime"; folder.mkdir()
    launcher = macos / EXECUTABLE
    safe_copy(source / "app", folder / "app")
    (folder / "app/ColorCorrect.App").chmod(0o755)
    shutil.copy2(source / "ccp-engine", folder / "ccp-engine"); (folder / "ccp-engine").chmod(0o755)
    for name in runtime["files"]:
        path = source / name
        if name.endswith(".dylib") and Path(name).name != name: raise ValueError("Runtime libraries must share the worker root")
        destination = folder / name; destination.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(path, destination)
    shutil.copy2(source / "gpu-runtime-manifest.json", resources / "upstream-gpu-runtime-manifest.json")
    for relative in (*MODELS, *REFERENCES, *REQUIRED_NOTICES, *DOCS, "models/manifest.json"):
        path = root / relative
        if path.is_file():
            target = folder / relative; target.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(path, target)
        elif relative in (*MODELS, *REFERENCES, *REQUIRED_NOTICES): raise FileNotFoundError(path)
    (folder / "python").mkdir(); shutil.copy2(wheel, folder / "python" / wheel.name)
    for path in (root / "api/examples").glob("*.py"):
        target = folder / "python/examples" / path.name; target.parent.mkdir(exist_ok=True); shutil.copy2(path, target)
    if notices is None:
        notices = output / "dependency-notices"
        collect(root, RID, source / "app", notices, root / "_implementation/notice-cache", fetch_notices)
    notice_report = validate_notices(notices, RID, root, source / "app")
    safe_copy(notices, folder / "licenses/dependencies")
    launcher_source = output / "launcher.m"; launcher_source.write_text(LAUNCHER, encoding="utf-8")
    run(["xcrun", "clang", "-arch", "arm64", "-mmacosx-version-min=14.0", "-fobjc-arc", "-O2",
         "-framework", "Foundation", launcher_source, "-o", launcher])
    launcher.chmod(0o755)
    # Native entry points avoid code-signing script xattrs, which ZIP transport
    # cannot reliably preserve. Each is signed independently before the bundle.
    for name in ("setup", "check-system"):
        path = folder / name; shutil.copy2(launcher, path); path.chmod(0o755)
    png = (root / "app/ColorCorrect.App/Assets/app.png").read_bytes()
    if png[:8] != b"\x89PNG\r\n\x1a\n" or struct.unpack(">II", png[16:24]) != (512, 512): raise ValueError("Expected the reviewed 512px application icon")
    (resources / "app.icns").write_bytes(b"icns" + struct.pack(">I", len(png)+16) + b"ic09" + struct.pack(">I",len(png)+8) + png)
    info = {"CFBundleExecutable": EXECUTABLE, "CFBundleIdentifier": "org.colorcorrect.studio", "CFBundleName": "ColorCorrect Studio",
            "CFBundleDisplayName": "ColorCorrect Studio", "CFBundlePackageType": "APPL", "CFBundleShortVersionString": version,
            "CFBundleVersion": version, "CFBundleIconFile": "app.icns", "LSMinimumSystemVersion": "14.0", "LSArchitecturePriority": ["arm64"],
            "NSHighResolutionCapable": True, "NSPrincipalClass": "NSApplication", "NSHumanReadableCopyright": "Copyright © 2026 Collins Wakholi"}
    with (bundle / "Contents/Info.plist").open("wb") as stream: plistlib.dump(info, stream)
    (folder / "INSTALL.txt").write_text("Apple silicon, macOS 14+. Move ColorCorrect Studio.app to Applications, then open it.\nThis build is ad-hoc signed and not notarized. macOS may require an explicit Security & Privacy approval.\nNo security settings or quarantine attributes are changed by this package.\nPython: pip install the wheel in Contents/Resources/runtime/python; Client(root='/Applications/ColorCorrect Studio.app').\nRaw Engine uses root='/Applications/ColorCorrect Studio.app/Contents/Resources/runtime'.\nCheck setup: Contents/Resources/runtime/setup --diagnose --verify. All private runtimes are bundled.\n", encoding="utf-8")
    audit = relocate_native(contents)
    removed = [path.relative_to(folder).as_posix() for path in folder.rglob("*") if path.is_file() and
               ("torch" in path.name.lower() or path.name.lower().startswith("libc10") or path.name.lower() in ("ffmpeg", "ffprobe"))]
    if removed: raise ValueError("Removed runtime/media payloads cannot be distributed: " + ", ".join(removed))
    (folder / "dependency-report.json").write_text(json.dumps(audit, indent=2), encoding="utf-8")
    entitlements = output / "runtime-entitlements.plist"
    with entitlements.open("wb") as stream: plistlib.dump({"com.apple.security.cs.allow-jit": True}, stream)
    for path in native_files(contents):
        if path == launcher: continue
        args = ["codesign", "--force", "--sign", "-", "--timestamp=none"]
        if path == folder / "app/ColorCorrect.App": args += ["--entitlements", entitlements]
        run([*args, path])
    final_runtime = dict(runtime)
    final_runtime["files"] = {name: digest(folder / name) for name in runtime["files"]}
    final_runtime["transformations"] = {name: {"upstream_sha256": original, "bundle_sha256": final_runtime["files"][name], "operations": "arm64 materialization, private dyld paths and ad-hoc signature"}
        for name, original in runtime["files"].items() if original != final_runtime["files"][name]}
    (folder / "gpu-runtime-manifest.json").write_text(json.dumps(final_runtime, indent=2), encoding="utf-8")
    common = {"schema": 2, "rid": RID, "version": version, "source": source_identity(root), "distribution_policy": "application",
              "architecture": "arm64", "minimum_os": "macOS 14.0", "rust_target": "aarch64-apple-darwin", "media_scope": "still images",
              "signing": {"kind": "ad-hoc", "notarized": False}, "native_validation": "pending", "worker_features_declared": ["ort-plane", "wgpu-lut", "rust-inference"],
              "inference_policy": "Auto GPU then explicit CPU inference; forced GPU engines fail clearly", "notice_inventory": {"components": len(notice_report.get("components", [])), "unresolved": []}}
    runtime_hashes = {path.relative_to(folder).as_posix(): digest(path) for path in sorted(folder.rglob("*")) if path.is_file()}
    (folder / "package-manifest.json").write_text(json.dumps({**common, "hash_scope": "private runtime; launcher and bundle signatures covered by external distribution manifest", "sha256": runtime_hashes}, indent=2), encoding="utf-8")
    run(["codesign", "--force", "--sign", "-", "--timestamp=none", "--entitlements", entitlements, bundle])
    run(["codesign", "--verify", "--deep", "--strict", bundle])
    # Outer bundle signing changes its main executable and resource envelope.
    # Only now can the complete external distribution manifest be hashed.
    manifest = {**common, "payload_root": f"{APP_NAME}/Contents/Resources/runtime", "sha256": {path.relative_to(payload).as_posix(): digest(path) for path in sorted(payload.rglob("*")) if path.is_file()}}
    (output / "package-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    prefix = output / f"ColorCorrect-Studio-{version}-{RID}"
    archive_zip(payload, Path(str(prefix) + ".zip"))
    def portable_owner(info):
        info.uid = info.gid = 0
        info.uname = info.gname = ""
        return info
    with tarfile.open(str(prefix) + ".tar.gz", "x:gz") as archive:
        archive.add(bundle, arcname=APP_NAME, recursive=True, filter=portable_owner)
    archives = [Path(str(prefix) + ".zip"), Path(str(prefix) + ".tar.gz")]
    (output / "SHA256SUMS").write_text("".join(f"{digest(path)}  {path.name}\n" for path in archives), encoding="ascii")
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", action="store_true", help="explicitly opt into application binary distribution")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--source-build", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--wheel", type=Path)
    parser.add_argument("--dependency-notices", type=Path)
    parser.add_argument("--fetch-notices", action="store_true")
    args = parser.parse_args()
    if not args.binary: parser.error("Binary packaging requires --binary; source validation uses build_macos.sh")
    if not all((args.source_build, args.output, args.wheel)): parser.error("--source-build, --output and --wheel are required")
    manifest = build(args.root, args.source_build, args.output, args.wheel, args.dependency_notices, args.fetch_notices)
    print(json.dumps({"rid": manifest["rid"], "version": manifest["version"], "output": str(args.output), "signing": manifest["signing"]}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
