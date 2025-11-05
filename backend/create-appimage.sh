#!/bin/bash
# Create Linux AppImage for ColorCorrector
# AppImage is a universal Linux package format (no installation needed)

set -e

echo "=========================================="
echo "ColorCorrector - Linux AppImage Creator"
echo "=========================================="
echo ""

APP_NAME="ColorCorrector"
APP_VERSION="1.0.0"
DIST_DIR="dist"
APPDIR="${APP_NAME}.AppDir"
APPIMAGE_NAME="${APP_NAME}-Linux-x86_64.AppImage"

# Check if dist folder exists
if [ ! -d "${DIST_DIR}/${APP_NAME}" ]; then
    echo "ERROR: PyInstaller build not found at ${DIST_DIR}/${APP_NAME}"
    echo "Please run: pyinstaller -y colorcorrector.spec"
    exit 1
fi

echo "[1/5] Creating AppDir structure..."
rm -rf "${APPDIR}"
mkdir -p "${APPDIR}/usr/bin"
mkdir -p "${APPDIR}/usr/lib"
mkdir -p "${APPDIR}/usr/share/applications"
mkdir -p "${APPDIR}/usr/share/icons/hicolor/256x256/apps"

echo "[2/5] Copying application files..."
cp -R "${DIST_DIR}/${APP_NAME}"/* "${APPDIR}/usr/bin/"

echo "[3/5] Creating desktop entry..."
cat > "${APPDIR}/usr/share/applications/${APP_NAME}.desktop" << EOF
[Desktop Entry]
Type=Application
Name=${APP_NAME}
Comment=Color Correction Package UI - Image Color Correction Tool
Exec=${APP_NAME}
Icon=${APP_NAME}
Categories=Graphics;Photography;
Terminal=false
StartupNotify=true
EOF

# Copy desktop file to AppDir root (required by AppImage)
cp "${APPDIR}/usr/share/applications/${APP_NAME}.desktop" "${APPDIR}/"

echo "[4/5] Creating AppRun launcher..."
cat > "${APPDIR}/AppRun" << 'APPRUN_EOF'
#!/bin/bash
# AppImage launcher for ColorCorrector

HERE="$(dirname "$(readlink -f "${0}")")"
export PATH="${HERE}/usr/bin:${PATH}"
export LD_LIBRARY_PATH="${HERE}/usr/lib:${LD_LIBRARY_PATH}"

cd "${HERE}/usr/bin"

# Start the server in background
./ColorCorrector &
SERVER_PID=$!

# Wait a moment for server to start
sleep 3

# Try to open browser (various methods for different Linux distros)
if command -v xdg-open > /dev/null; then
    xdg-open "http://localhost:5000" &
elif command -v gnome-open > /dev/null; then
    gnome-open "http://localhost:5000" &
elif command -v kde-open > /dev/null; then
    kde-open "http://localhost:5000" &
else
    echo "ColorCorrector server started at http://localhost:5000"
    echo "Please open this URL in your browser."
fi

# Keep the app running
wait $SERVER_PID
APPRUN_EOF

chmod +x "${APPDIR}/AppRun"

# Create a simple icon (placeholder - use a real icon in production)
echo "[5/5] Creating placeholder icon..."
# In production, you'd copy a real PNG icon here
# For now, create a minimal SVG that gets converted
cat > "${APPDIR}/${APP_NAME}.svg" << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<svg width="256" height="256" xmlns="http://www.w3.org/2000/svg">
  <rect width="256" height="256" fill="#4A90E2"/>
  <text x="128" y="140" font-size="72" fill="white" text-anchor="middle" font-family="Arial">CC</text>
</svg>
EOF

# Copy icon to proper location
cp "${APPDIR}/${APP_NAME}.svg" "${APPDIR}/usr/share/icons/hicolor/256x256/apps/${APP_NAME}.svg"

echo "[6/6] Downloading appimagetool..."
APPIMAGETOOL="appimagetool-x86_64.AppImage"
if [ ! -f "${APPIMAGETOOL}" ]; then
    wget -q --show-progress "https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"
    chmod +x "${APPIMAGETOOL}"
fi

echo "[7/7] Building AppImage..."
ARCH=x86_64 ./"${APPIMAGETOOL}" "${APPDIR}" "${APPIMAGE_NAME}"

echo ""
echo "=========================================="
echo "SUCCESS!"
echo "=========================================="
echo "AppImage created: ${APPIMAGE_NAME}"
echo ""
echo "You can now:"
echo "  1. Test: chmod +x ${APPIMAGE_NAME} && ./${APPIMAGE_NAME}"
echo "  2. Share: Upload to GitHub releases"
echo ""
echo "Users can run it directly without installation:"
echo "  chmod +x ${APPIMAGE_NAME}"
echo "  ./${APPIMAGE_NAME}"
echo ""
