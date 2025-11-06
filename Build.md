# Build Instructions

## Prerequisites
- Python 3.12+
- Node.js 18+
- npm or yarn
- Platform-specific tools (see below)

## Multi-Architecture Support

This project supports multiple architectures:
- **Windows**: x64 (Intel/AMD) and ARM64 (Windows on ARM)
- **macOS**: x86_64 (Intel) and arm64 (Apple Silicon M1/M2/M3)
- **Linux**: x86_64 (Intel/AMD) and aarch64 (ARM64)

The build system automatically detects your architecture and builds accordingly.

## Quick Build (All Platforms)

### 1. Build Frontend
```bash
cd frontend
npm install
npm run build
cd ..
```

### 2. Build Backend Executable
```bash
cd backend
pip install -r requirements.txt
pip install pyinstaller

# Copy frontend build
rm -rf frontend_dist
cp -r ../frontend/dist frontend_dist

# Build with PyInstaller (auto-detects architecture)
pyinstaller -y colorcorrector.spec
```

### 3. Create Platform Installer

**Windows:**
```bash
# Install Inno Setup first: https://jrsoftware.org/isinfo.php
# For x64:
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DARCH_SUFFIX=x64 installer-windows.iss

# For ARM64:
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DARCH_ARM64 /DARCH_SUFFIX=arm64 installer-windows.iss
```

**macOS:**
```bash
chmod +x create-dmg.sh
./create-dmg.sh
# Output: ColorCorrector-macOS-{arch}.dmg
```

**Linux:**
```bash
chmod +x create-appimage.sh
./create-appimage.sh
# Output: ColorCorrector-Linux-{arch}.AppImage
```

## Architecture-Specific Builds

### Cross-Compilation (Advanced)

To build for a different architecture than your host:

```bash
# Set target architecture before building
export PYINSTALLER_TARGET_ARCH=arm64  # or x86_64, x64, aarch64
pyinstaller -y colorcorrector.spec
```

**Note**: Cross-compilation has limitations:
- Python dependencies must be available for target architecture
- Some native libraries may not support cross-compilation
- macOS can build universal binaries on ARM hosts

## Manual Build Steps

### Windows Build (x64 or ARM64)

1. **Install Dependencies**
   ```bash
   cd frontend
   npm install
   
   cd ../backend
   pip install -r requirements.txt
   pip install pyinstaller
   ```

2. **Build Frontend**
   ```bash
   cd frontend
   npm run build
   cd ../backend
   xcopy /E /I ..\frontend\dist frontend_dist
   ```

3. **Build Backend Executable**
   ```bash
   # For native architecture
   pyinstaller -y colorcorrector.spec
   
   # For specific architecture (advanced)
   set PYINSTALLER_TARGET_ARCH=x64
   pyinstaller -y colorcorrector.spec
   ```

4. **Create Installer**
   ```powershell
   # Install Inno Setup from https://jrsoftware.org/isinfo.php
   & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DARCH_SUFFIX=x64 installer-windows.iss
   ```

5. **Output**
   - Installer: `backend/installer_output/ColorCorrector-Setup-Windows-x64.exe`

### macOS Build (Intel or Apple Silicon)

1. **Install Dependencies**
   ```bash
   cd frontend
   npm install
   
   cd ../backend
   pip3 install -r requirements.txt
   pip3 install pyinstaller
   ```

2. **Build Frontend**
   ```bash
   cd frontend
   npm run build
   cd ../backend
   rm -rf frontend_dist
   cp -r ../frontend/dist frontend_dist
   ```

3. **Build Backend Executable**
   ```bash
   # Native architecture (recommended)
   pyinstaller -y colorcorrector.spec
   
   # For universal binary (requires dependencies for both archs)
   export PYINSTALLER_TARGET_ARCH=universal2
   pyinstaller -y colorcorrector.spec
   ```

4. **Create DMG**
   ```bash
   chmod +x create-dmg.sh
   ./create-dmg.sh
   ```

5. **Output**
   - Intel: `ColorCorrector-macOS-x86_64.dmg`
   - Apple Silicon: `ColorCorrector-macOS-arm64.dmg`
   - Universal: `ColorCorrector-macOS-universal2.dmg`

**Apple Silicon Notes:**
- Build on M1/M2/M3 Mac for native ARM64 binary
- Native builds are faster and more reliable than Rosetta-translated builds
- Ensure all dependencies (especially PyTorch, NumPy) have ARM64 support

### Linux Build (x86_64 or ARM64)

1. **Install Dependencies**
   ```bash
   cd frontend
   npm install
   
   cd ../backend
   pip3 install -r requirements.txt
   pip3 install pyinstaller
   ```

2. **Build Frontend**
   ```bash
   cd frontend
   npm run build
   cd ../backend
   rm -rf frontend_dist
   cp -r ../frontend/dist frontend_dist
   ```

3. **Build Backend Executable**
   ```bash
   # Native architecture
   pyinstaller -y colorcorrector.spec
   ```

4. **Create AppImage**
   ```bash
   chmod +x create-appimage.sh
   ./create-appimage.sh
   ```

5. **Output**
   - x86_64: `ColorCorrector-Linux-x86_64.AppImage`
   - ARM64: `ColorCorrector-Linux-aarch64.AppImage`

**ARM64 Linux Notes:**
- Build on ARM64 hardware (Raspberry Pi 4/5, ARM server)
- Ensure all Python dependencies have ARM64 wheels available
- May need to compile some packages from source

## Testing Installers

### Testing on Different Architectures

**macOS M1/M2/M3 (Apple Silicon):**
```bash
# Download the arm64 DMG
# Open and drag to Applications
# First run: Right-click → Open
# Server should start in ~3-5 seconds
# Check log: cat ~/.colorcorrector/launcher.log
```

**Windows ARM64:**
```powershell
# Download the arm64 installer
# Run the installer
# Launch from Start Menu
# Check if server starts properly
```

**Linux ARM64:**
```bash
# Download the aarch64 AppImage
chmod +x ColorCorrector-Linux-aarch64.AppImage
./ColorCorrector-Linux-aarch64.AppImage
# Browser should open automatically
```

### Common Issues and Solutions

**macOS: "App can't be opened"**
- Solution: Right-click → Open (first time only)
- Or: System Settings → Privacy & Security → Open Anyway

**macOS M1: Backend doesn't start**
- Check: Are you using the arm64 DMG (not x86_64)?
- Check log: `cat ~/.colorcorrector/launcher.log`
- Ensure no other process is using port 5000

**Windows: "Windows protected your PC"**
- Click "More info" → "Run anyway"
- App is unsigned (code signing requires paid certificate)

**Linux: "Permission denied"**
- Run: `chmod +x ColorCorrector-*.AppImage`

## Notes

- The PyInstaller spec file (`colorcorrector.spec`) automatically bundles the frontend build
- Architecture is auto-detected; use `PYINSTALLER_TARGET_ARCH` env var to override
- GPU/CUDA support is optional and increases installer size significantly
- All installers are self-contained with no external dependencies
- The executable includes Python interpreter, all dependencies, and the frontend

## Docker image

We provide a Dockerfile at `backend/Dockerfile` that builds the frontend, copies it into the backend, installs Python runtime dependencies and runs the Flask app.

Build and run locally:

```bash
# Build the image (from repo root)
docker build -t collins137/colorcorrector:local -f backend/Dockerfile .

# Run the container (maps host 5000 -> container 5000)
docker run --rm -p 5000:5000 collins137/colorcorrector:local
```

Notes about pushing to Docker Hub
- The supplied GitHub Actions workflow (`.github/workflows/windows-docker.yml`) will build a Docker image and push to Docker Hub when the repository has the following repository secrets configured:
   - `DOCKERHUB_USERNAME` — your Docker Hub username (e.g., `collins137`)
   - `DOCKERHUB_TOKEN` — a Docker Hub access token or password

Once secrets are set, pushing to `main` (or manually triggering the workflow) will build and push the image `collins137/colorcorrector:latest`.

## GitHub Actions CI

The project uses GitHub Actions for automated multi-architecture builds:

### Build Matrix

`.github/workflows/build-artifacts.yml` builds the following configurations:

| Platform | Architecture | Runner | Installer Output |
|----------|-------------|--------|------------------|
| Windows | x64 | windows-latest | `ColorCorrector-Setup-Windows-x64.exe` |
| Windows | arm64 | windows-latest-arm64* | `ColorCorrector-Setup-Windows-arm64.exe` |
| macOS | x86_64 | macos-13 | `ColorCorrector-macOS-x86_64.dmg` |
| macOS | arm64 | macos-14 | `ColorCorrector-macOS-arm64.dmg` |
| Linux | x86_64 | ubuntu-latest | `ColorCorrector-Linux-x86_64.AppImage` |
| Linux | aarch64 | ubuntu-latest-arm64* | `ColorCorrector-Linux-aarch64.AppImage` |

\* ARM64 runners marked as experimental - may require self-hosted runners

### Automated Release Process

1. Push to `main` branch or manually trigger workflow
2. Frontend builds once
3. Backend builds in parallel for all architectures
4. Platform-specific installers created
5. All artifacts uploaded to GitHub release
6. Release tagged with date and build number

### Workflow Configuration

The workflow automatically:
- Detects architecture and sets `PYINSTALLER_TARGET_ARCH`
- Builds frontend and copies to backend
- Installs CPU-only PyTorch (smaller installers)
- Creates platform-specific installers
- Uploads artifacts with architecture in filename
- Creates GitHub release with all installers

### Triggering Builds

```bash
# Manual trigger via GitHub CLI
gh workflow run build-artifacts.yml

# Or push to main
git push origin main
```

## Docker Build

We provide a Dockerfile at `backend/Dockerfile` that builds the frontend, copies it into the backend, installs Python runtime dependencies and runs the Flask app.

Build and run locally:

```bash
# Build the image (from repo root)
docker build -t collins137/colorcorrector:local -f backend/Dockerfile .

# Run the container (maps host 5000 -> container 5000)
docker run --rm -p 5000:5000 collins137/colorcorrector:local
```

### Multi-Architecture Docker Images

To build for multiple architectures:

```bash
# Setup buildx
docker buildx create --use

# Build for multiple platforms
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t collins137/colorcorrector:latest \
  -f backend/Dockerfile \
  --push \
  .
```

Notes about pushing to Docker Hub:
- The supplied GitHub Actions workflow will build a Docker image and push to Docker Hub when the repository has the following repository secrets configured:
  - `DOCKERHUB_USERNAME` — your Docker Hub username (e.g., `collins137`)
  - `DOCKERHUB_TOKEN` — a Docker Hub access token or password

Once secrets are set, pushing to `main` (or manually triggering the workflow) will build and push the image `collins137/colorcorrector:latest`.
