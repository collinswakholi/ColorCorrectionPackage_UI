# backend/colorcorrector.spec
# Build with: pyinstaller -y colorcorrector.spec
import os
import importlib.util
from PyInstaller.utils.hooks import (
    collect_submodules,
    collect_dynamic_libs,
    collect_data_files,
)

block_cipher = None
backend_dir = os.path.dirname(os.path.abspath(SPEC))
frontend_dist = os.path.join(backend_dir, "frontend_dist")
models_dir = os.path.join(backend_dir, "models")

# Hidden imports required by scientific stack and Flask
hiddenimports = []
def _collect_without_tests(package_name: str):
    return collect_submodules(
        package_name,
        filter=lambda fullname: "tests" not in fullname and not fullname.endswith(".tests"),
    )


hiddenimports += _collect_without_tests("sklearn")
hiddenimports += _collect_without_tests("matplotlib")
hiddenimports += collect_submodules("cv2")
hiddenimports += _collect_without_tests("scipy")
hiddenimports += _collect_without_tests("pandas")
hiddenimports += _collect_without_tests("numpy")
hiddenimports += _collect_without_tests("PIL")
hiddenimports += [
    "flask",
    "flask_cors",
    "flask_compress",
    "ColorCorrectionPipeline",
    "ColorCorrectionPipeline.core",
]

# Binary dependencies (DLLs / shared libraries)
binaries = []
binaries += collect_dynamic_libs("cv2")
binaries += collect_dynamic_libs("scipy")
binaries += collect_dynamic_libs("sklearn")

# Data files required at runtime
datas = []
datas += collect_data_files("sklearn")
datas += collect_data_files("matplotlib")
datas += collect_data_files("cv2")

if os.path.isdir(frontend_dist):
    datas.append((frontend_dist, "frontend_dist"))
if os.path.isdir(models_dir):
    datas.append((models_dir, "models"))

# Ensure packaged ColorCorrectionPipeline model assets are bundled
cc_spec = importlib.util.find_spec("ColorCorrectionPipeline")
if cc_spec and cc_spec.origin:
    cc_root = os.path.dirname(cc_spec.origin)
    yolo_model = os.path.join(cc_root, "flat_field", "models", "plane_det_model_YOLO_512_n.pt")
    if os.path.isfile(yolo_model):
        datas.append((yolo_model, os.path.join("ColorCorrectionPipeline", "flat_field", "models")))

analysis = Analysis(
    ["server_enhanced.py"],
    pathex=[backend_dir],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[os.path.join(backend_dir, "pyinstaller_hooks")],
    hooksconfig={},
    runtime_hooks=[],
    excludes=["tkinter", "PySide6", "PyQt5", "statsmodels"],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(analysis.pure, analysis.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    analysis.scripts,
    [],
    exclude_binaries=True,
    name="ColorCorrector",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=True,
    disable_windowed_traceback=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)

coll = COLLECT(
    exe,
    analysis.binaries,
    analysis.zipfiles,
    analysis.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name="ColorCorrector",
)
