"""Linux runtime dependencies: one list drives check-system, install-dependencies and the .deb."""
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
from linux_runtime import (deb_depends, glibc_floor_violations, glibc_requirements, library_entries,  # noqa: E402
                           read_runtime_dependencies)

LINUX = ROOT / "installer/linux"
OS_RELEASES = {
    "ubuntu_24_04": 'ID=ubuntu\nVERSION_ID="24.04"\nPRETTY_NAME="Ubuntu 24.04.5 LTS"\n',
    "ubuntu_22_04": 'ID=ubuntu\nVERSION_ID="22.04"\nPRETTY_NAME="Ubuntu 22.04.5 LTS"\n',
    "debian_13": 'ID=debian\nVERSION_ID="13"\nPRETTY_NAME="Debian GNU/Linux 13 (trixie)"\n',
    "debian_12": 'ID=debian\nVERSION_ID="12"\nPRETTY_NAME="Debian GNU/Linux 12 (bookworm)"\n',
}


def elf_with_versions(*versions, weak=()):
    """A minimal 64-bit little-endian ELF whose .gnu.version_r needs libc.so.6 at ``versions``."""
    names = ["libc.so.6", *versions, *weak]
    strtab = b"\0"
    offsets = {}
    for name in names:
        offsets[name] = len(strtab)
        strtab += name.encode() + b"\0"
    labels = [(v, 0) for v in versions] + [(v, 0x2) for v in weak]
    aux = b""
    for i, (label, flags) in enumerate(labels):
        following = 16 if i < len(labels) - 1 else 0
        aux += struct.pack("<IHHII", 0, flags, 0, offsets[label], following)
    verneed = struct.pack("<HHIII", 1, len(labels), offsets["libc.so.6"], 16, 0) + aux
    header_size, section_size = 64, 64
    strtab_at = header_size
    verneed_at = strtab_at + len(strtab)
    shoff = verneed_at + len(verneed)
    sections = [
        struct.pack("<IIQQQQIIQQ", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        struct.pack("<IIQQQQIIQQ", 0, 3, 0, 0, strtab_at, len(strtab), 0, 0, 1, 0),
        struct.pack("<IIQQQQIIQQ", 0, 0x6FFFFFFE, 0, 0, verneed_at, len(verneed), 1, 1, 8, 0),
    ]
    header = bytearray(header_size)
    header[:6] = b"\x7fELF\x02\x01"
    struct.pack_into("<Q", header, 0x28, shoff)
    struct.pack_into("<HH", header, 0x3A, section_size, len(sections))
    return bytes(header) + strtab + verneed + b"".join(sections)


class RuntimeDependencyListTests(unittest.TestCase):
    def setUp(self):
        self.values = read_runtime_dependencies()

    def test_every_supported_distro_has_packages_and_a_name(self):
        distros = self.values["CCP_SUPPORTED_DISTROS"].split()
        self.assertEqual(sorted(distros), sorted(OS_RELEASES))
        lengths = {len(self.values["CCP_PACKAGES_" + d].split()) for d in distros}
        self.assertEqual(len(lengths), 1, "per-distro lists must align for .deb alternatives")

    def test_runtime_loaded_libraries_are_listed(self):
        # Avalonia.X11 12 loads these by name; ldd never sees them (the 0.4.0 false pass).
        sonames = {soname for soname, _ in library_entries(self.values)}
        for soname in ("libX11.so.6", "libICE.so.6", "libSM.so.6", "libXrandr.so.2", "libXi.so.6",
                       "libXcursor.so.1", "libXext.so.6", "libGL.so.1", "libgtk-3.so.0",
                       "libfontconfig.so.1", "libicuuc.so.*", "libssl.so.3"):
            self.assertIn(soname, sonames)
        self.assertIn("libvulkan.so.1", {s for s, _ in library_entries(self.values, "CCP_OPTIONAL_LIBS")})

    def test_deb_depends_resolve_on_every_distro(self):
        depends = deb_depends(self.values)
        self.assertEqual(depends[0], "libc6 (>= 2.35)")
        self.assertIn("libicu74 | libicu70 | libicu76 | libicu72", depends)
        self.assertIn("libgtk-3-0t64 | libgtk-3-0", depends)
        for package in ("libxcursor1", "libxext6", "libice6", "libsm6"):
            self.assertIn(package, depends)
        self.assertEqual(deb_depends(self.values, ("libdrm2",))[-1], "libdrm2")


class GlibcFloorTests(unittest.TestCase):
    def test_reads_required_versions_and_ignores_weak_ones(self):
        with tempfile.TemporaryDirectory() as folder:
            binary = Path(folder) / "ccp-engine"
            binary.write_bytes(elf_with_versions("GLIBC_2.2.5", "GLIBC_2.34", weak=("GLIBC_2.39",)))
            self.assertEqual(glibc_requirements(binary), {"2.2.5", "2.34"})
            self.assertEqual(glibc_floor_violations(Path(folder), "2.35"), [])

    def test_flags_a_binary_built_against_a_newer_glibc(self):
        with tempfile.TemporaryDirectory() as folder:
            (Path(folder) / "ccp-engine").write_bytes(elf_with_versions("GLIBC_2.39"))
            (Path(folder) / "notes.txt").write_text("not an ELF file")
            self.assertEqual(glibc_floor_violations(Path(folder), "2.35"),
                             ["ccp-engine needs GLIBC_2.39 (floor 2.35)"])


@unittest.skipUnless(sys.platform.startswith("linux") and shutil.which("sh"), "POSIX shell scripts")
class LinuxScriptTests(unittest.TestCase):
    def run_script(self, name, *args, os_release):
        with tempfile.TemporaryDirectory() as folder:
            release = Path(folder) / "os-release"
            release.write_text(os_release)
            env = dict(os.environ, CCP_OS_RELEASE=str(release))
            return subprocess.run(["sh", str(LINUX / name), *args], capture_output=True, text=True, env=env)

    def test_install_dependencies_previews_the_right_packages_per_distro(self):
        values = read_runtime_dependencies()
        for distro, release in OS_RELEASES.items():
            result = self.run_script("install-dependencies", os_release=release)
            self.assertEqual(result.returncode, 0, result.stderr)
            for package in values["CCP_PACKAGES_" + distro].split():
                self.assertIn(package, result.stdout)
            self.assertIn("Nothing was changed", result.stdout)

    def test_install_dependencies_refuses_unsupported_distros(self):
        result = self.run_script("install-dependencies", os_release='ID=fedora\nVERSION_ID="44"\nPRETTY_NAME="Fedora 44"\n')
        self.assertEqual(result.returncode, 1)
        self.assertIn("Fedora 44", result.stderr)

    def test_scripts_parse(self):
        for name in ("check-system", "install-dependencies", "runtime-dependencies"):
            self.assertEqual(subprocess.run(["sh", "-n", str(LINUX / name)]).returncode, 0, name)


if __name__ == "__main__":
    unittest.main()
