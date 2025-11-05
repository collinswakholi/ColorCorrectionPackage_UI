# Color Correction Package UI

A modern web-based user interface for the [ColorCorrectionPackage](https://github.com/collinswakholi/ColorCorrectionPackage).

## Overview

This application provides an intuitive React frontend paired with a Flask backend to perform color correction on images using the ColorCorrectionPackage pipeline. Upload images, detect color charts, run color correction, and visualize results—all from your browser.

## Screenshot

![Color Correction UI](Ui_animation.webp)
*Upload images, detect color charts, and visualize correction results in real-time*

## Download Pre-built Executables

Get the latest release for your operating system.

### Latest Release

Visit the [Releases page](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/latest) to download:

- **Windows**: Download `ColorCorrector-Setup-Windows.exe`
  - Double-click the installer
  - Follow the installation wizard
  - Launch from Start Menu or Desktop shortcut
  - Browser opens automatically to `http://localhost:5000`
  - *Note: May show Windows Defender warning (unsigned app) - click "More info" → "Run anyway"*

- **macOS**: Download `ColorCorrector-macOS.dmg`
  - Open the DMG file
  - Drag ColorCorrector to Applications folder
  - Launch from Applications
  - Browser opens automatically to `http://localhost:5000`
  - *Note: First run requires right-click → "Open" (unsigned app)*

- **Linux**: Download `ColorCorrector-Linux-x86_64.AppImage`
  - Make executable: `chmod +x ColorCorrector-Linux-x86_64.AppImage`
  - Run: `./ColorCorrector-Linux-x86_64.AppImage`
  - Browser opens automatically to `http://localhost:5000`
  - *No installation required - runs directly*

All installers are self-contained with the web interface included. No dependencies, no manual setup!

## Features

- **Eash image upload** with batch processing support
- **Automatic color chart detection** using the ColorCorrectionPipeline
- **One-click color correction** with visual before/after comparison
- **Interactive scatter plots** showing correction quality
- **Downloadable results** (corrected images and CSV reports)
- **Cross-platform packaging** (Windows EXE, macOS, Linux, Docker)

## Quick Start

### Docker (Recommended)

```bash
docker pull collinswakholi/colorcorrector:latest
docker run -p 5000:5000 collinswakholi/colorcorrector:latest
```

Open [http://localhost:5000](http://localhost:5000) in your browser.

### Local Development

**Prerequisites:** Python 3.12+, Node.js 18+

1. **Backend:**
   ```bash
   cd backend
   pip install -r requirements.txt
   python server_enhanced.py
   ```

2. **Frontend:**
   ```bash
   cd frontend
   npm install
   npm run dev
   ```

Visit [http://localhost:5173](http://localhost:5173) (Vite dev) or [http://localhost:5000](http://localhost:5000) (Flask serves built frontend).

## Building

### Standalone Executable

```bash
cd frontend && npm run build
cd ../backend
pyinstaller -y colorcorrector.spec
```

The executable will be in `backend/dist/ColorCorrector/`.

### Docker Image

```bash
docker build -t colorcorrector -f backend/Dockerfile .
```

## Architecture

- **Frontend:** React + Vite + Tailwind CSS
- **Backend:** Flask REST API wrapping the ColorCorrectionPipeline
- **Core Library:** [ColorCorrectionPackage](https://github.com/collinswakholi/ColorCorrectionPackage)

## License

See [LICENSE.txt](LICENSE.txt)

## Credits

This UI implementation wraps the [ColorCorrectionPackage](https://github.com/collinswakholi/ColorCorrectionPackage) by Collins Wakholi.
