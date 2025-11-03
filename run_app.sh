#!/bin/bash

# ======================================================================
#   Color Correction Studio - Production Launcher
# ======================================================================

EXE_PATH="backend/dist/colorcorrector"

echo "======================================================================"
echo "  Color Correction Studio"
echo "======================================================================"

# Check if executable exists
if [ ! -f "$EXE_PATH" ]; then
    echo "ERROR: Backend executable not found at $EXE_PATH"
    echo ""
    echo "Build steps:"
    echo "  1. cd frontend"
    echo "  2. npm run build"
    echo "  3. cd ../backend"
    echo "  4. pyinstaller -y colorcorrector.spec"
    echo ""
    exit 1
fi

# Make executable if not already
chmod +x "$EXE_PATH"

echo "Starting backend server..."

# Start backend in background
"$EXE_PATH" &
BACKEND_PID=$!

echo "Backend started (PID $BACKEND_PID)"

# Wait for backend to be ready
echo "Waiting for backend to be ready..."
MAX_RETRIES=30
RETRY=0

while [ $RETRY -lt $MAX_RETRIES ]; do
    if curl -s http://localhost:5000/api/health > /dev/null 2>&1; then
        echo "Backend is ready."
        break
    fi
    RETRY=$((RETRY + 1))
    sleep 1
done

if [ $RETRY -eq $MAX_RETRIES ]; then
    echo "ERROR: Backend failed to start after 30 seconds"
    kill -9 $BACKEND_PID 2>/dev/null
    exit 1
fi

echo ""
echo "======================================================================"
echo "  Opening application in browser..."
echo "======================================================================"
echo "  URL: http://localhost:5000"
echo ""
echo "  Press Ctrl+C to stop the application"
echo "======================================================================"
echo ""

# Open browser based on OS
if [[ "$OSTYPE" == "darwin"* ]]; then
    # macOS
    open http://localhost:5000
elif [[ "$OSTYPE" == "linux-gnu"* ]]; then
    # Linux
    if command -v xdg-open > /dev/null; then
        xdg-open http://localhost:5000
    elif command -v gnome-open > /dev/null; then
        gnome-open http://localhost:5000
    else
        echo "Please open http://localhost:5000 in your browser"
    fi
fi

echo "Application is running. Press Ctrl+C to stop."
echo ""

# Trap Ctrl+C to cleanup
trap "echo ''; echo 'Shutting down...'; kill -9 $BACKEND_PID 2>/dev/null; echo 'Application stopped.'; exit 0" INT TERM

# Wait for backend process
wait $BACKEND_PID

echo ""
echo "Application stopped."
