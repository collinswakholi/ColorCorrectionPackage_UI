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

# Detect architecture
ARCH=$(uname -m)
case "$ARCH" in
    x86_64)
        ARCH_NAME="x86_64"
        ;;
    arm64)
        ARCH_NAME="arm64"
        ;;
    *)
        echo "WARNING: Unknown architecture: $ARCH, using generic name"
        ARCH_NAME=$ARCH
        ;;
esac

DMG_NAME="${APP_NAME}-macOS-${ARCH_NAME}.dmg"
echo "Building DMG for architecture: $ARCH_NAME"

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
# Supports both x86_64 and ARM64 (M1/M2/M3) architectures

set -e

# Get the directory of this script
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

# Detect architecture for debugging
ARCH=$(uname -m)
echo "ColorCorrector starting on $ARCH architecture..."

# Log file in user's home directory
LOG_FILE="$HOME/.colorcorrector/launcher.log"
mkdir -p "$(dirname "$LOG_FILE")"

{
    echo "===== ColorCorrector Launch Log ====="
    echo "Date: $(date)"
    echo "Architecture: $ARCH"
    echo "Script directory: $SCRIPT_DIR"
    echo "======================================"
    
    # Check if server executable exists
    if [ ! -f "./ColorCorrector_server" ]; then
        echo "ERROR: ColorCorrector_server executable not found!"
        echo "Expected location: $SCRIPT_DIR/ColorCorrector_server"
        osascript -e 'display dialog "ColorCorrector Error: Server executable not found. Please reinstall the application." buttons {"OK"} default button "OK" with icon stop'
        exit 1
    fi
    
    # Make sure the server is executable
    chmod +x "./ColorCorrector_server"
    
    # Start the ColorCorrector server in background
    echo "Starting ColorCorrector server..."
    ./ColorCorrector_server >> "$LOG_FILE" 2>&1 &
    SERVER_PID=$!
    
    echo "Server PID: $SERVER_PID"
    
    # Wait for server to start (increased timeout and better checking)
    MAX_RETRIES=60
    RETRY_COUNT=0
    SERVER_READY=false
    
    while [ $RETRY_COUNT -lt $MAX_RETRIES ]; do
        # Check if server process is still running
        if ! ps -p $SERVER_PID > /dev/null 2>&1; then
            echo "ERROR: Server process died unexpectedly"
            echo "Last 20 lines of log:"
            tail -20 "$LOG_FILE"
            osascript -e 'display dialog "ColorCorrector Error: Server failed to start. Check log at: '"$LOG_FILE"'" buttons {"OK"} default button "OK" with icon stop'
            exit 1
        fi
        
        # Check if server is responding
        if curl -s -f http://localhost:5000/api/health > /dev/null 2>&1; then
            echo "Server started successfully (attempt $RETRY_COUNT)"
            SERVER_READY=true
            break
        fi
        
        sleep 1
        RETRY_COUNT=$((RETRY_COUNT + 1))
    done
    
    if [ "$SERVER_READY" = false ]; then
        echo "ERROR: Server did not respond after $MAX_RETRIES seconds"
        echo "Last 20 lines of log:"
        tail -20 "$LOG_FILE"
        osascript -e 'display dialog "ColorCorrector Error: Server timeout. Check log at: '"$LOG_FILE"'" buttons {"OK"} default button "OK" with icon stop'
        kill $SERVER_PID 2>/dev/null || true
        exit 1
    fi
    
    # Open the default browser
    echo "Opening browser..."
    open "http://localhost:5000"
    
    echo "ColorCorrector is now running. Close this window to stop the server."
    
    # Keep the app running and monitor the server
    wait $SERVER_PID
    
} >> "$LOG_FILE" 2>&1
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
