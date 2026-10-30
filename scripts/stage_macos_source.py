"""Finish a local Apple-silicon source build; never creates a distribution archive.

The worker, checked runtime and resources share one explicit SDK root. Keeping
the runtime only in a launch-script environment is insufficient: an SDK client
with an explicit root deliberately refuses runtimes outside its worker folder.
"""
from pathlib import Path
import argparse
import shlex
import shutil

from package_native import require_architecture
from package_support import RUNTIMES, check_model_manifest, check_runtime_manifest


def stage(root: Path, output: Path, worker: Path | None = None) -> None:
    root, output = root.resolve(strict=True), output.resolve(strict=True)
    if any(output.is_relative_to(root / resource) for resource in ("models", "refs")):
        raise ValueError("Local build output cannot be inside copied model/reference resources")
    check_runtime_manifest(output, "osx-arm64", "Darwin")
    check_model_manifest(root)
    worker = (worker or root / "engine/target/release/ccp-engine").resolve(strict=True)
    app = output / "app/ColorCorrect.App"
    require_architecture(worker, ("Darwin", "arm64"))
    require_architecture(app, ("Darwin", "arm64"))
    for filename in RUNTIMES["Darwin"]:
        require_architecture(output / filename, ("Darwin", "arm64"))
    # Copies keep the local build self-consistent if another build later changes
    # the source tree's target directory or model files.
    shutil.copy2(worker, output / "ccp-engine")
    for resource in ("models", "refs"):
        shutil.copytree(root / resource, output / resource)
    script = "#!/bin/sh\nset -eu\n" + "".join(
        "export " + key + "=" + shlex.quote(str(value)) + "\n"
        for key, value in {
            "CCP_NEW_UI_ROOT": output,
            "CCP_ENGINE_EXE": output / "ccp-engine",
            "ORT_DYLIB_PATH": output / "libonnxruntime.dylib",
            "DYLD_LIBRARY_PATH": output,
        }.items()
    )
    script += "exec " + shlex.quote(str(app)) + ' "$@"\n'
    launcher = output / "launch"
    launcher.write_text(script, encoding="utf-8", newline="\n")
    launcher.chmod(0o755)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--worker", type=Path, help="worker from the explicit source-build Cargo target directory")
    args = parser.parse_args()
    stage(args.root, args.output, args.worker)
