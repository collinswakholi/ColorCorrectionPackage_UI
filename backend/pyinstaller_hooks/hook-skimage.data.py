"""Custom PyInstaller hook to bypass skimage.data recursion.

Collecting skimage.data submodules triggers crashes on some Windows builds
when PyInstaller spawns an isolated interpreter. This hook intentionally
exposes no extra hidden imports so the default contrib hook is ignored.
"""

hiddenimports = []
