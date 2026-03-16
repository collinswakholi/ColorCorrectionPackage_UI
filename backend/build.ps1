<#
.SYNOPSIS
    Build ColorCorrector EXE with resource throttling, warning suppression, and code signing.
.PARAMETER SkipSign
    Skip code signing entirely.
.PARAMETER MaxCpuPercent
    Maximum percentage of CPU cores to use (default: 60).
#>
param(
    [switch]$SkipSign,
    [int]$MaxCpuPercent = 60
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Push-Location $scriptDir

function Write-Step { param([string]$msg) Write-Host "`n========== $msg ==========" -ForegroundColor Cyan }

# -- CPU Throttling ---------------------------------------------------------------
$totalCores = [Environment]::ProcessorCount
$coresToUse = [Math]::Max(1, [Math]::Floor($totalCores * $MaxCpuPercent / 100))
$affinityMask = 0
for ($i = 0; $i -lt $coresToUse; $i++) { $affinityMask = $affinityMask -bor (1 -shl $i) }
Write-Host "CPU Throttle: $coresToUse / $totalCores cores ($MaxCpuPercent%)" -ForegroundColor Magenta

# -- Suppress warnings ------------------------------------------------------------
$env:PYTHONWARNINGS = "ignore::DeprecationWarning,ignore::FutureWarning,ignore::UserWarning"
$env:NUMBA_DISABLE_PERFORMANCE_WARNINGS = "1"

# -- Check frontend ---------------------------------------------------------------
Write-Step "1/5  Checking frontend"
if (-not (Test-Path "frontend_dist\index.html")) {
    Write-Host "  Building frontend..." -ForegroundColor Yellow
    Push-Location "..\frontend"
    & npm ci 2>&1 | Out-Null
    & npm run build 2>&1
    Pop-Location
    if (Test-Path "..\frontend\dist") {
        Copy-Item -Recurse -Force "..\frontend\dist" "frontend_dist"
        Write-Host "  [OK] Frontend built" -ForegroundColor Green
    } else {
        Write-Host "  [ERROR] Frontend build failed" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "  [OK] frontend_dist exists" -ForegroundColor Green
}

# -- Code-signing certificate -----------------------------------------------------
Write-Step "2/5  Code-signing certificate"
$certThumbprint = $null
$selfSignedPfx = Join-Path $scriptDir "colorcorrector_selfsigned.pfx"

if ($SkipSign) {
    Write-Host "  Skipped (-SkipSign)" -ForegroundColor Yellow
} else {
    $existingCert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -like "*ColorCorrector*" } |
        Select-Object -First 1
    if ($existingCert) {
        $certThumbprint = $existingCert.Thumbprint
        Write-Host "  [OK] Reusing cert: $certThumbprint" -ForegroundColor Green
    } else {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert `
            -Subject "CN=ColorCorrector Self-Signed, O=Collins Wakholi" `
            -CertStoreLocation Cert:\CurrentUser\My `
            -NotAfter (Get-Date).AddYears(3) `
            -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")
        $certThumbprint = $cert.Thumbprint
        Write-Host "  [OK] Created self-signed cert: $certThumbprint (valid 3 years)" -ForegroundColor Green
        Write-Host "  NOTE: Other machines will see 'Unknown Publisher' - normal for self-signed." -ForegroundColor Yellow
        Write-Host "  For production: buy from DigiCert/Sectigo/GlobalSign." -ForegroundColor Yellow
        # Export PFX for CI use
        $pwd = ConvertTo-SecureString "ColorCorrector2026" -Force -AsPlainText
        Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$certThumbprint" -FilePath $selfSignedPfx -Password $pwd | Out-Null
        Write-Host "  Exported PFX: colorcorrector_selfsigned.pfx (pw: ColorCorrector2026)" -ForegroundColor Gray
    }
}

# -- PyInstaller build (throttled) ------------------------------------------------
Write-Step "3/5  PyInstaller build (throttled to $MaxCpuPercent%)"
if (Test-Path "build") { Remove-Item -Recurse -Force "build" }
if (Test-Path "dist")  { Remove-Item -Recurse -Force "dist" }
Write-Host "  Cleaned previous build/dist" -ForegroundColor Gray

$buildLog = Join-Path $scriptDir "build.log"

# Resolve python from the venv (handles case where subshell loses venv activation)
$pythonExe = $null
$venvPython = Join-Path $scriptDir "..\..\..\.ccvenv\Scripts\python.exe"
# Also check 2 levels up (backend -> ColorCorrectionPackage_UI -> Optimum_CC_python)
$venvPython2 = Join-Path $scriptDir "..\..\.ccvenv\Scripts\python.exe"
if (Test-Path $venvPython) {
    $pythonExe = (Resolve-Path $venvPython).Path
} elseif (Test-Path $venvPython2) {
    $pythonExe = (Resolve-Path $venvPython2).Path
} elseif (Get-Command python -ErrorAction SilentlyContinue) {
    $pythonExe = (Get-Command python).Source
} else {
    Write-Host "  [ERROR] Python not found! Activate your venv first." -ForegroundColor Red
    exit 1
}
Write-Host "  Using Python: $pythonExe" -ForegroundColor Gray

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $pythonExe
$psi.Arguments = "-W ignore -m PyInstaller -y colorcorrector.spec"
$psi.WorkingDirectory = $scriptDir
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
$proc.Start() | Out-Null

try {
    $proc.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
    $proc.ProcessorAffinity = [IntPtr]$affinityMask
    Write-Host "  PID=$($proc.Id), priority=BelowNormal, cores=$coresToUse" -ForegroundColor Gray
} catch {
    Write-Host "  Could not set affinity (non-critical): $_" -ForegroundColor Yellow
}

# Stream progress while building
$spinChars = @('|','/','-','\')
$si = 0
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while (-not $proc.HasExited) {
    $elapsed = $sw.Elapsed.ToString("mm\:ss")
    Write-Host "`r  Building... $($spinChars[$si % 4])  [$elapsed]" -NoNewline -ForegroundColor White
    $si++
    Start-Sleep -Milliseconds 500
}
$sw.Stop()
$elapsed = $sw.Elapsed.ToString("mm\:ss")
Write-Host "`r  Building... done! [$elapsed]              " -ForegroundColor Green

$stdout = $proc.StandardOutput.ReadToEnd()
$stderr = $proc.StandardError.ReadToEnd()
"$stdout`n$stderr" | Out-File $buildLog -Encoding utf8

# Count warnings in log
$warningCount = ($stderr -split "`n" | Where-Object { $_ -match "WARNING|UserWarning|DeprecationWarning|FutureWarning" }).Count
Write-Host "  Warnings suppressed/filtered: $warningCount" -ForegroundColor Gray

if ($proc.ExitCode -ne 0) {
    Write-Host "  [ERROR] PyInstaller failed (exit $($proc.ExitCode))" -ForegroundColor Red
    Write-Host "  Last 25 lines of output:" -ForegroundColor Yellow
    $stderr -split "`n" | Select-Object -Last 25 | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    Write-Host "  Full log: $buildLog" -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path "dist\ColorCorrector\ColorCorrector.exe")) {
    Write-Host "  [ERROR] EXE not found after build!" -ForegroundColor Red
    exit 1
}
$distSize = [Math]::Round(((Get-ChildItem "dist\ColorCorrector" -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Write-Host "  [OK] dist\ColorCorrector\ ($distSize MB)" -ForegroundColor Green
Write-Host "  Full log: $buildLog" -ForegroundColor Gray

# -- Sign EXE + DLLs --------------------------------------------------------------
Write-Step "4/5  Code signing"
if ($SkipSign -or -not $certThumbprint) {
    Write-Host "  Skipped" -ForegroundColor Yellow
} else {
    $sigCert = Get-Item "Cert:\CurrentUser\My\$certThumbprint"

    # Sign main exe
    $sig = Set-AuthenticodeSignature -FilePath "dist\ColorCorrector\ColorCorrector.exe" `
        -Certificate $sigCert -TimestampServer "http://timestamp.digicert.com" -HashAlgorithm SHA256
    if ($sig.Status -eq "Valid" -or $sig.Status -eq "UnknownError") {
        Write-Host "  [OK] Signed ColorCorrector.exe" -ForegroundColor Green
    } else {
        Write-Host "  [WARN] Signing result: $($sig.Status) - $($sig.StatusMessage)" -ForegroundColor Yellow
    }

    # Sign DLLs/PYDs in _internal
    $dlls = Get-ChildItem "dist\ColorCorrector\_internal" -Recurse -Include "*.dll","*.pyd" -ErrorAction SilentlyContinue
    if ($dlls) {
        $count = 0; $failed = 0
        foreach ($dll in $dlls) {
            try {
                Set-AuthenticodeSignature -FilePath $dll.FullName -Certificate $sigCert -HashAlgorithm SHA256 2>&1 | Out-Null
                $count++
            } catch { $failed++ }
            if ($count % 100 -eq 0 -and $count -gt 0) {
                Write-Host "`r  Signed $count / $($dlls.Count) DLLs..." -NoNewline -ForegroundColor Gray
            }
        }
        Write-Host "`r  [OK] Signed $count / $($dlls.Count) DLLs/PYDs ($failed skipped)     " -ForegroundColor Green
    }
}

# -- Inno Setup installer (optional) ----------------------------------------------
Write-Step "5/5  Installer (Inno Setup)"
$iscc = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host "  Inno Setup not found - skipping installer" -ForegroundColor Yellow
    Write-Host "  Install from: https://jrsoftware.org/isinfo.php" -ForegroundColor Gray
    Write-Host "  Or just zip dist\ColorCorrector\ for testing" -ForegroundColor Gray
} else {
    Write-Host "  Found: $iscc" -ForegroundColor Gray
    & $iscc /DARCH_SUFFIX=x64 "$scriptDir\installer-windows.iss"
    if ($LASTEXITCODE -eq 0) {
        $installer = Get-ChildItem "installer_output\*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($installer -and $certThumbprint -and -not $SkipSign) {
            $sigCert = Get-Item "Cert:\CurrentUser\My\$certThumbprint"
            Set-AuthenticodeSignature -FilePath $installer.FullName -Certificate $sigCert `
                -TimestampServer "http://timestamp.digicert.com" -HashAlgorithm SHA256 | Out-Null
            Write-Host "  [OK] Signed $($installer.Name)" -ForegroundColor Green
        }
        Write-Host "  Installer: $($installer.FullName)" -ForegroundColor Green
    } else {
        Write-Host "  [ERROR] Inno Setup failed" -ForegroundColor Red
    }
}

# -- Summary -----------------------------------------------------------------------
Write-Host "`n========== BUILD COMPLETE ==========" -ForegroundColor Green
Write-Host "  EXE:  dist\ColorCorrector\ColorCorrector.exe" -ForegroundColor White
Write-Host "  Size: $distSize MB" -ForegroundColor White
if ($certThumbprint) {
    Write-Host "  Signed: YES ($certThumbprint)" -ForegroundColor Green
} else {
    Write-Host "  Signed: NO" -ForegroundColor Yellow
}
Write-Host "  Log:  $buildLog" -ForegroundColor Gray
Write-Host "`n  To test: copy dist\ColorCorrector\ to the target machine and run ColorCorrector.exe" -ForegroundColor Cyan
Write-Host ""

# Cleanup build intermediates
if (Test-Path "build") { Remove-Item -Recurse -Force "build" }

Pop-Location
