import base64
import hashlib
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from reproducible_wheel import normalize  # noqa: E402

INFO = "demo-1.0.dist-info"


def build(path: Path, newline: bytes, when, mode: int, system: int):
    """A wheel the way one build machine would write it (0.5.0 shipped five variants)."""
    files = {
        "demo.py": b"import os" + newline + b"VALUE = 1" + newline,
        f"{INFO}/METADATA": b"Metadata-Version: 2.4" + newline + b"Name: demo" + newline,
        f"{INFO}/licenses/LICENSE": b"MIT" + newline,
        f"{INFO}/WHEEL": b"Wheel-Version: 1.0\nTag: py3-none-any\n",
    }
    record = "".join(f"{name},,\n" for name in files) + f"{INFO}/RECORD,,\n"
    with zipfile.ZipFile(path, "w") as target:
        for name, data in [*files.items(), (f"{INFO}/RECORD", record.encode())]:
            info = zipfile.ZipInfo(name, date_time=when)
            info.create_system = system
            info.external_attr = (mode & 0xFFFF) << 16
            target.writestr(info, data, compress_type=zipfile.ZIP_DEFLATED)


class ReproducibleWheelTests(unittest.TestCase):
    def test_windows_and_linux_builds_become_identical(self):
        with tempfile.TemporaryDirectory() as tmp:
            windows, linux = Path(tmp, "windows.whl"), Path(tmp, "linux.whl")
            build(windows, b"\r\n", (2026, 11, 26, 4, 12, 58), 0o100666, 0)
            build(linux, b"\n", (2026, 11, 26, 3, 28, 32), 0o100644, 3)
            self.assertNotEqual(windows.read_bytes(), linux.read_bytes())
            self.assertEqual(normalize(windows), normalize(linux))
            self.assertEqual(windows.read_bytes(), linux.read_bytes())
            self.assertEqual(normalize(windows), hashlib.sha256(linux.read_bytes()).hexdigest(), "normalizing is idempotent")

    def test_record_matches_every_file_and_text_uses_lf(self):
        with tempfile.TemporaryDirectory() as tmp:
            wheel = Path(tmp, "demo.whl")
            build(wheel, b"\r\n", (2026, 1, 2, 3, 4, 5), 0o100666, 0)
            normalize(wheel)
            with zipfile.ZipFile(wheel) as archive:
                names = archive.namelist()
                self.assertEqual(f"{INFO}/RECORD", names[-1])
                rows = [line.split(",") for line in archive.read(f"{INFO}/RECORD").decode().splitlines()]
                for name, digest, size in rows:
                    if name.endswith("RECORD"):
                        self.assertEqual(("", ""), (digest, size))
                        continue
                    data = archive.read(name)
                    self.assertNotIn(b"\r\n", data)
                    expected = base64.urlsafe_b64encode(hashlib.sha256(data).digest()).rstrip(b"=").decode()
                    self.assertEqual(("sha256=" + expected, str(len(data))), (digest, size))
                for info in archive.infolist():
                    self.assertEqual((1980, 1, 1, 0, 0, 0), info.date_time)
                    self.assertEqual(0o100644, info.external_attr >> 16)


if __name__ == "__main__":
    unittest.main()
