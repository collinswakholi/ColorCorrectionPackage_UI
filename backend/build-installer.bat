@echo off
REM Build Windows Installer for ColorCorrector
REM Prerequisites:
REM   1. Inno Setup 6.x installed (https://jrsoftware.org/isinfo.php)
REM   2. PyInstaller build completed (dist/ColorCorrector folder exists)

echo ========================================
echo ColorCorrector - Windows Installer Build
echo ========================================
echo.

REM Check if dist folder exists
if not exist "dist\ColorCorrector" (
    echo ERROR: PyInstaller build not found!
    echo Please run: pyinstaller -y colorcorrector.spec
    echo.
    pause
    exit /b 1
)

echo [1/3] Checking for Inno Setup...
set ISCC="C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if not exist %ISCC% (
    set ISCC="C:\Program Files\Inno Setup 6\ISCC.exe"
)
if not exist %ISCC% (
    echo ERROR: Inno Setup not found!
    echo Please install from: https://jrsoftware.org/isinfo.php
    echo Default location: C:\Program Files (x86)\Inno Setup 6\
    echo.
    pause
    exit /b 1
)

echo Found: %ISCC%
echo.

echo [2/3] Building installer with Inno Setup...
echo Running: %ISCC% installer-windows.iss
echo.
%ISCC% installer-windows.iss

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ERROR: Installer build failed!
    pause
    exit /b 1
)

echo.
echo [3/3] Build complete!
echo.
echo ========================================
echo SUCCESS!
echo ========================================
echo Installer location: backend\installer_output\ColorCorrector-Setup-Windows.exe
echo.
echo You can now:
echo   1. Test the installer on your local machine
echo   2. Share the installer with others
echo   3. Upload to GitHub releases
echo.
pause
