"""PyInstaller hook for colour-checker-detection.

Ensures the .npz template files (e.g. template_colorchecker_classic.npz) and
all submodules are bundled into the frozen executable.
"""

from PyInstaller.utils.hooks import collect_data_files, collect_submodules

datas = collect_data_files("colour_checker_detection")
hiddenimports = collect_submodules("colour_checker_detection")
