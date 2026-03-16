# backend/colorcorrector.spec
# Build with: pyinstaller -y colorcorrector.spec
#
# This creates a STANDALONE executable package that includes:
# - Python interpreter (no Python installation required)
# - All dependencies (NumPy, OpenCV, scikit-learn, Flask, etc.)
# - CPU-only PyTorch (no GPU/CUDA dependencies)
# - Frontend assets (React web interface)
# - Color correction models
#
# Users can run the executable without installing Python, Node.js, or any dependencies.
#
import os
import importlib.util
from PyInstaller.utils.hooks import (
    collect_submodules,
    collect_dynamic_libs,
    collect_data_files,
)

block_cipher = None

# Prevent ultralytics from auto-installing CLIP and other deps during collect_submodules
os.environ["YOLO_AUTOINSTALL"] = "0"

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
hiddenimports += _collect_without_tests("numba")
# Collect torch/torchvision but aggressively filter unused modules
_torch_exclude_patterns = [
    "torch.testing._internal", "torch.distributed", "torch.onnx",
    "torch.quantization", "torch.nn.quantized", "torch.nn.quantizable",
    "torch.profiler", "torch.package", "torch.xpu",
    "torch.ao", "torch.export", "torch.compiler",
    "torch.contrib", "torch.fx", "torch.masked",
    "torch.monitor", "torch.multiprocessing",
    "torch._dynamo", "torch._functorch", "torch._inductor",
    "torch._lazy", "torch._subclasses",
    "torch.utils.tensorboard", "torch.utils.benchmark",
]
_torch_subs = collect_submodules("torch")
_torch_subs = [m for m in _torch_subs if not any(x in m for x in _torch_exclude_patterns)]
hiddenimports += _torch_subs
# Only collect torchvision modules actually used (models, transforms, ops, io)
hiddenimports += collect_submodules("torchvision")
hiddenimports += _collect_without_tests("colour")
hiddenimports += collect_submodules("colour_checker_detection")
hiddenimports += _collect_without_tests("ultralytics")
# plotly: only collect core + graph_objs, skip massive validators tree (loaded lazily)
_plotly_subs = _collect_without_tests("plotly")
_plotly_subs = [m for m in _plotly_subs if "plotly.validators" not in m]
hiddenimports += _plotly_subs
hiddenimports += _collect_without_tests("seaborn")
hiddenimports += collect_submodules("flask")
hiddenimports += collect_submodules("flask_cors")
hiddenimports += collect_submodules("flask_compress")
hiddenimports += collect_submodules("psutil")
# CCP: auto-discover all 20 submodules instead of incomplete manual list
hiddenimports += collect_submodules("ColorCorrectionPipeline")

# Binary dependencies (DLLs / shared libraries)
binaries = []
binaries += collect_dynamic_libs("cv2")
binaries += collect_dynamic_libs("scipy")
binaries += collect_dynamic_libs("sklearn")
binaries += collect_dynamic_libs("torch")
binaries += collect_dynamic_libs("PIL")
binaries += collect_dynamic_libs("numba")

# Collect system OpenGL libraries if available (needed for OpenCV)
# These are typically system dependencies, but we try to bundle them
import sys
if sys.platform.startswith('linux'):
    import sysconfig
    import glob
    # Detect the multiarch tuple (e.g., x86_64-linux-gnu, aarch64-linux-gnu)
    _multiarch = sysconfig.get_config_var('MULTIARCH') or 'x86_64-linux-gnu'
    _lib_dir = f'/usr/lib/{_multiarch}'
    # Try to find and include OpenGL libraries
    _gl_lib_names = ['libGL.so.1', 'libGLX.so.0', 'libglib-2.0.so.0', 'libgomp.so.1']
    for _lib_name in _gl_lib_names:
        _full_path = os.path.join(_lib_dir, _lib_name)
        if os.path.exists(_full_path):
            binaries.append((_full_path, '.'))
        else:
            # Try glob pattern for versioned .so files
            _stem = _lib_name.split('.so')[0]
            matches = glob.glob(os.path.join(_lib_dir, _stem + '.so*'))
            if matches:
                binaries.append((matches[0], '.'))

# Filter out CUDA/NVIDIA binaries (keep CPU-only torch)
def filter_cuda_binaries(bins):
    """Remove CUDA/NVIDIA binaries from the list to reduce size"""
    if not bins:
        return bins
    cuda_patterns = ['nvidia', 'cublas', 'cudnn', 'cufft', 'curand', 'cusolver', 'cusparse', 'nccl', 'nvrtc', 'cupti']
    # Binaries can be 2-tuple (src, dest) or 3-tuple (dest, src, type)
    filtered = []
    for item in bins:
        if len(item) == 2:
            src, dest = item
            if not any(pattern in dest.lower() or pattern in src.lower() for pattern in cuda_patterns):
                filtered.append(item)
        elif len(item) == 3:
            dest, src, typ = item
            if not any(pattern in dest.lower() or pattern in src.lower() for pattern in cuda_patterns):
                filtered.append(item)
        else:
            filtered.append(item)  # Keep unknown formats
    return filtered

binaries = filter_cuda_binaries(binaries)

# Filter out .lib static library files (not needed at runtime, only for linking)
binaries = [b for b in binaries if not b[0].lower().endswith('.lib')]

# Data files required at runtime
datas = []
datas += collect_data_files("sklearn")
datas += collect_data_files("matplotlib")
datas += collect_data_files("cv2")
datas += collect_data_files("torch")
datas += collect_data_files("PIL")
datas += collect_data_files("numba")
datas += collect_data_files("ColorCorrectionPipeline")
datas += collect_data_files("colour")
datas += collect_data_files("colour_checker_detection")
datas += collect_data_files("ultralytics")
datas += collect_data_files("plotly")
datas += collect_data_files("seaborn")
datas += collect_data_files("scipy")

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
    ["server_enhanced.py", "scatter_plot_utils.py"],
    pathex=[backend_dir],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[os.path.join(backend_dir, "pyinstaller_hooks")],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[
        "tkinter", "PySide6", "PyQt5", "statsmodels",
        # CUDA / NVIDIA
        "nvidia", "nvidia.cuda", "nvidia.cudnn", "nvidia.cublas",
        "nvidia.cufft", "nvidia.curand", "nvidia.cusolver", "nvidia.cusparse",
        # CLIP and its deps
        "clip", "ftfy", "regex",
        # Heavy torch internals
        "torchaudio",
        "torch.utils.tensorboard", "torch.utils.benchmark",
        # Dev tools pulled in transitively
        "IPython", "jupyter", "notebook", "nbformat", "nbconvert",
        "pytest", "_pytest", "py", "pluggy",
        "lxml", "docutils", "sphinx",
        "pygame", "pygments",
    ],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

# Filter torch data files: remove .lib files and test data that are not needed at runtime
datas = [d for d in datas if not d[0].lower().endswith('.lib')]

pyz = PYZ(analysis.pure, analysis.zipped_data, cipher=block_cipher)

# --- onedir mode: faster startup (no multi-GB extraction), same total size ---
exe = EXE(
    pyz,
    analysis.scripts,
    [],          # no binaries/datas in EXE for onedir
    name="ColorCorrector",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=True,
    disable_windowed_traceback=False,
    target_arch=os.environ.get('PYINSTALLER_TARGET_ARCH', None),
    codesign_identity=None,
    entitlements_file=None,
)

coll = COLLECT(
    exe,
    analysis.binaries,
    analysis.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name="ColorCorrector",
)
