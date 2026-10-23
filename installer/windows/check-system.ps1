param([switch]$InstallPrerequisites)
$ErrorActionPreference = 'Stop'
$packageRoot = $PSScriptRoot
$expected = (Get-Content -LiteralPath (Join-Path $packageRoot 'package-architecture') -Raw).Trim()
$machine = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
$actual = if ($machine -eq 'ARM64') { 'arm64' } elseif ($machine -eq 'AMD64') { 'x64' } else { 'unsupported' }
if ($actual -ne $expected) { Write-Error "This package requires $expected; this machine is $actual."; exit 1 }
$metadata = Get-Content -LiteralPath (Join-Path $packageRoot 'prerequisites/vc-redist.json') -Raw | ConvertFrom-Json
$key = "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\$actual"
$installed = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
$ready = $installed -and $installed.Installed -eq 1 -and [version]$installed.Version.TrimStart('v') -ge [version]$metadata.version
if (-not $ready -and $InstallPrerequisites) {
    $redist = Join-Path $packageRoot 'prerequisites/vc_redist.exe'
    $signature = Get-AuthenticodeSignature -LiteralPath $redist
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation(?:,|$)' -or
        (Get-FileHash -Algorithm SHA256 -LiteralPath $redist).Hash -ne $metadata.sha256) { throw 'VC prerequisite verification failed.' }
    Write-Host 'The Microsoft runtime installer may request administrator permission. No other software will be installed.'
    $process = Start-Process -FilePath $redist -ArgumentList '/install', '/passive', '/norestart' -Verb RunAs -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -eq 3010) { Write-Host 'Restart Windows to finish installing the runtime.'; exit 3010 }
    if ($process.ExitCode -ne 0 -and $process.ExitCode -ne 1638) { throw "Microsoft runtime installation failed: $($process.ExitCode)" }
    $installed = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
    $ready = $installed -and $installed.Installed -eq 1 -and [version]$installed.Version.TrimStart('v') -ge [version]$metadata.version
}
if (-not $ready) {
    Write-Host "Microsoft Visual C++ runtime $($metadata.version) or later is required."
    Write-Host 'To install the included prerequisite, run: powershell -NoProfile -File .\check-system.ps1 -InstallPrerequisites'
    exit 1
}
foreach ($name in 'ccp-engine.exe','onnxruntime.dll','onnxruntime_providers_webgpu.dll','DirectML.dll','app/ColorCorrect.App.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $name) -PathType Leaf)) { throw "Incomplete app package: $name" }
}
Write-Host 'Application prerequisites are present. GPU availability is checked separately by the worker.'
