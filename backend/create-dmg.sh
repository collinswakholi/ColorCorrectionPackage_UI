#!/bin/bash
# Create macOS DMG installer for ColorCorrector
# This script creates a proper .app bundle and DMG installer

set -e

echo "=========================================="
echo "ColorCorrector - macOS DMG Creator"
echo "=========================================="
echo ""

APP_NAME="ColorCorrector"
APP_VERSION="1.0.0"
DIST_DIR="dist"
APP_BUNDLE="${APP_NAME}.app"
DMG_NAME="${APP_NAME}-macOS.dmg"

# Check if dist folder exists
if [ ! -d "${DIST_DIR}/${APP_NAME}" ]; then
    echo "ERROR: PyInstaller build not found at ${DIST_DIR}/${APP_NAME}"
    echo "Please run: pyinstaller -y colorcorrector.spec"
    exit 1
fi

echo "[1/5] Creating .app bundle structure..."
rm -rf "${APP_BUNDLE}"
mkdir -p "${APP_BUNDLE}/Contents/MacOS"
mkdir -p "${APP_BUNDLE}/Contents/Resources"

echo "[2/5] Copying executable and dependencies..."
cp -R "${DIST_DIR}/${APP_NAME}"/* "${APP_BUNDLE}/Contents/MacOS/"

echo "[3/5] Creating Info.plist..."
cat > "${APP_BUNDLE}/Contents/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>${APP_NAME}</string>
    <key>CFBundleIdentifier</key>
    <string>com.collinswakholi.colorcorrector</string>
    <key>CFBundleName</key>
    <string>${APP_NAME}</string>
    <key>CFBundleVersion</key>
    <string>${APP_VERSION}</string>
    <key>CFBundleShortVersionString</key>
    <string>${APP_VERSION}</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleSignature</key>
    <string>????</string>
    <key>LSMinimumSystemVersion</key>
    <string>10.13</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSAppleScriptEnabled</key>
    <false/>
    <key>LSUIElement</key>
    <false/>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
</dict>
</plist>
EOF

echo "[4/5] Creating launcher script..."
# Create a launcher script that opens the browser
cat > "${APP_BUNDLE}/Contents/MacOS/${APP_NAME}_launcher.sh" << 'LAUNCHER_EOF'
#!/bin/bash
# ColorCorrector Launcher for macOS

cd "$(dirname "$0")"

# Start the ColorCorrector server in background
./ColorCorrector_server &
SERVER_PID=$!

# Wait for server to start
echo "Starting ColorCorrector server..."
for i in {1..30}; do
    if curl -s http://localhost:5000/api/health > /dev/null 2>&1; then
        echo "Server started successfully"
        break
    fi
    sleep 1
done

# Open the default browser
open "http://localhost:5000"

# Keep the app running
wait $SERVER_PID
LAUNCHER_EOF

chmod +x "${APP_BUNDLE}/Contents/MacOS/${APP_NAME}_launcher.sh"

# Rename original executable and use launcher as main entry
mv "${APP_BUNDLE}/Contents/MacOS/${APP_NAME}" "${APP_BUNDLE}/Contents/MacOS/${APP_NAME}_server"
mv "${APP_BUNDLE}/Contents/MacOS/${APP_NAME}_launcher.sh" "${APP_BUNDLE}/Contents/MacOS/${APP_NAME}"

echo "[5/5] Creating DMG..."
# Remove old DMG if exists
rm -f "${DMG_NAME}"

# Create DMG using hdiutil (built-in macOS tool)
hdiutil create -volname "${APP_NAME}" -srcfolder "${APP_BUNDLE}" -ov -format UDZO "${DMG_NAME}"

# Optional: Use create-dmg for fancy DMG with background image
# Uncomment if create-dmg is installed: brew install create-dmg
# create-dmg \
#   --volname "${APP_NAME}" \
#   --window-pos 200 120 \
#   --window-size 600 400 \
#   --icon-size 100 \
#   --icon "${APP_NAME}.app" 175 120 \
#   --hide-extension "${APP_NAME}.app" \
#   --app-drop-link 425 120 \
#   "${DMG_NAME}" \
#   "${APP_BUNDLE}"

echo ""
echo "=========================================="
echo "SUCCESS!"
echo "=========================================="
echo "DMG created: ${DMG_NAME}"
echo ""
echo "You can now:"
echo "  1. Test: Open ${DMG_NAME} and drag to Applications"
echo "  2. Share: Upload to GitHub releases"
echo ""
echo "Note: The app is not code-signed. Users may need to:"
echo "  Right-click → Open (first time only)"
echo "  Or: System Preferences → Security & Privacy → Open Anyway"
echo ""
