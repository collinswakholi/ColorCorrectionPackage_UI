"""Build a native .deb (Ubuntu 24.04/22.04, Debian 13/12) from the same reviewed portable payload."""
from pathlib import Path
import re
import shutil
import subprocess

from linux_runtime import deb_depends, payload_dependencies


def build_deb(payload: Path, output: Path, version: str, rid: str) -> Path:
    if rid not in ("linux-x64", "linux-arm64") or not re.fullmatch(r"[0-9][0-9A-Za-z.+~:-]*", version):
        raise ValueError("Invalid Debian package target/version")
    arch = "amd64" if rid == "linux-x64" else "arm64"
    staging = output / "deb-root"
    target = staging / "opt/colorcorrect-studio"
    shutil.copytree(payload, target)
    control = staging / "DEBIAN"
    control.mkdir()
    extra = ("libdrm2", "libexpat1", "libzstd1") if (payload / "addons/wsl-gpu").exists() else ()
    depends = deb_depends(payload_dependencies(payload), extra)
    (control / "control").write_text(
        f"Package: colorcorrect-studio\nVersion: {version}\nArchitecture: {arch}\n"
        "Maintainer: ColorCorrect Studio contributors\nSection: graphics\nPriority: optional\n"
        f"Depends: {', '.join(depends)}\n"
        "Description: Chart-based still-image color correction\n"
        " Self-contained desktop application and native processing worker.\n"
        " CPU inference is included; compatible GPU acceleration is optional.\n", encoding="utf-8")
    applications = staging / "usr/share/applications"
    applications.mkdir(parents=True)
    (applications / "colorcorrect-studio.desktop").write_text(
        "[Desktop Entry]\nType=Application\nName=ColorCorrect Studio\n"
        "Comment=Chart-based still-image color correction\nExec=/opt/colorcorrect-studio/ColorCorrect-Studio\n"
        "Icon=colorcorrect-studio\nTerminal=false\nCategories=Graphics;Photography;\n", encoding="utf-8")
    icon_source = Path(__file__).parents[1] / "app/ColorCorrect.App/Assets/app.png"
    icons = staging / "usr/share/pixmaps"
    icons.mkdir(parents=True)
    shutil.copy2(icon_source, icons / "colorcorrect-studio.png")
    binary = staging / "usr/bin"
    binary.mkdir(parents=True)
    (binary / "colorcorrect-studio").symlink_to("/opt/colorcorrect-studio/ColorCorrect-Studio")
    destination = output / f"colorcorrect-studio_{version}_{arch}.deb"
    subprocess.run(["dpkg-deb", "--root-owner-group", "--build", str(staging), str(destination)], check=True)
    return destination
