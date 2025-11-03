@echo off
setlocal

set "EXE_PATH=backend\dist\ColorCorrector\ColorCorrector.exe"

if not exist "%EXE_PATH%" (
  echo [WARNING] Backend executable not found at "%EXE_PATH%".
  echo.
  echo The application needs to be built before it can run.
  echo.
  set /p BUILD_CHOICE="Would you like to build it now? (Y/N): "
  
  if /i "%BUILD_CHOICE%"=="Y" (
    echo.
    echo ======================================================================
    echo   Building Color Correction Studio
    echo ======================================================================
    echo.
    
    REM Step 1: Build frontend
    echo [1/2] Building frontend...
    cd frontend
    if errorlevel 1 (
      echo [ERROR] Failed to navigate to frontend directory.
      cd ..
      exit /b 1
    )
    
    call npm run build
    if errorlevel 1 (
      echo [ERROR] Frontend build failed. Please check the error messages above.
      cd ..
      exit /b 1
    )
    echo [SUCCESS] Frontend built successfully.
    echo.
    
    REM Step 2: Build backend
    echo [2/2] Building backend executable...
    cd ..\backend
    if errorlevel 1 (
      echo [ERROR] Failed to navigate to backend directory.
      cd ..
      exit /b 1
    )
    
    call pyinstaller -y colorcorrector.spec
    if errorlevel 1 (
      echo [ERROR] Backend build failed. Please check the error messages above.
      echo.
      echo Common issues:
      echo   - PyInstaller not installed: pip install pyinstaller
      echo   - Missing dependencies: pip install -r requirements.txt
      cd ..
      exit /b 1
    )
    echo [SUCCESS] Backend built successfully.
    echo.
    
    cd ..
    
    REM Verify the build was successful
    if not exist "%EXE_PATH%" (
      echo [ERROR] Build completed but executable still not found at "%EXE_PATH%".
      echo Please check the build output for errors.
      exit /b 1
    )
    
    echo ======================================================================
    echo   Build Complete!
    echo ======================================================================
    echo.
  ) else (
    echo.
    echo Build cancelled. Please run the build process manually:
    echo   1. cd frontend
    echo   2. npm run build
    echo   3. cd ..\backend
    echo   4. pyinstaller -y colorcorrector.spec
    exit /b 1
  )
)

echo ======================================================================
echo   Color Correction Studio
echo ======================================================================
echo Starting backend server...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "& { $exe = Resolve-Path '%EXE_PATH%'; $proc = Start-Process -FilePath $exe -PassThru; Write-Host ('Backend started (PID {0})' -f $proc.Id); $healthUri = 'http://127.0.0.1:5000/api/health'; Write-Host 'Waiting for backend to be ready...'; for ($i = 0; $i -lt 30; $i++) { try { Invoke-WebRequest -UseBasicParsing -Uri $healthUri | Out-Null; Write-Host 'Backend is ready.'; break } catch { Start-Sleep -Seconds 1 } }; Write-Host ''; Write-Host '======================================================================'; Write-Host '  Opening application in browser...'; Write-Host '======================================================================'; Write-Host '  URL: http://localhost:5000'; Write-Host ''; Write-Host '  Close the browser to stop the application'; Write-Host '======================================================================'; Write-Host ''; Start-Process 'http://127.0.0.1:5000'; Write-Host 'Application is running. Press Ctrl+C to stop.'; Write-Host ''; try { Wait-Process -Id $proc.Id } finally { if (-not $proc.HasExited) { Write-Host 'Shutting down backend...'; Stop-Process -Id $proc.Id -Force } } }"

echo.
echo Application stopped.
endlocal

