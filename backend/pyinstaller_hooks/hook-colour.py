"""PyInstaller hook for colour-science.

Ensures characterisation data, spectral datasets, and all submodules used by
ColorCorrectionPipeline are bundled into the frozen executable.
"""

from PyInstaller.utils.hooks import collect_data_files, collect_submodules

datas = collect_data_files("colour")
hiddenimports = collect_submodules("colour")
