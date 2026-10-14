# Stage a Windows CPU payload (and optional Inno installers). Does not commit.
param(
    [switch]$CudaAddon,
    [switch]$SkipFetch,
    [switch]$SkipPortableArchive,
    [switch]$BuildInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist\win-cpu"
$sdkDescriptor = Join-Path $root '.build-cache\sdk-cpu.json'
if (-not $SkipFetch) { & "$PSScriptRoot\fetch_libtorch.ps1" -Kind cpu -StageOnly }
if (-not (Test-Path -LiteralPath $sdkDescriptor)) { throw 'Fetch the complete CPU SDK before using -SkipFetch.' }
$libtorch = (Get-Content -LiteralPath $sdkDescriptor -Raw | ConvertFrom-Json).path
if (-not (Test-Path -LiteralPath (Join-Path $libtorch 'include\torch\csrc\api\include\torch\torch.h'))) { throw 'Build SDK lacks headers.' }

$savedEnvironment = @{}
foreach ($name in @('LIBTORCH','LIBTORCH_PATH','KMP_DUPLICATE_LIB_OK','LIBTORCH_BYPASS_VERSION_CHECK','PATH','RUSTFLAGS','CXXFLAGS','CFLAGS')) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
$env:LIBTORCH = $libtorch
$env:LIBTORCH_PATH = $libtorch
$env:KMP_DUPLICATE_LIB_OK = "TRUE"
$env:LIBTORCH_BYPASS_VERSION_CHECK = "1"
$env:PATH = (Join-Path $libtorch "lib") + ";" + $env:PATH
$env:RUSTFLAGS = ($env:RUSTFLAGS + ' --remap-path-prefix=' + $env:USERPROFILE + '=/build').Trim()
# MSVC's prefix trimming also removes __FILE__ paths from torch-sys C++ headers.
$env:CXXFLAGS = ($env:CXXFLAGS + ' /Brepro /d1trimfile:' + $env:USERPROFILE).Trim()
$env:CFLAGS = ($env:CFLAGS + ' /Brepro /d1trimfile:' + $env:USERPROFILE).Trim()

Push-Location (Join-Path $root "engine")
try {
    cargo build --locked -p ccp_bridge --release --features tch,ort-plane,video
    if ($LASTEXITCODE -ne 0) { throw "cargo release build failed" }
} finally {
    Pop-Location
}

$expectedDist = [IO.Path]::GetFullPath((Join-Path $root 'dist\win-cpu'))
if ([IO.Path]::GetFullPath($dist) -ne $expectedDist) { throw 'Refusing to clean an unexpected payload path.' }
if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

dotnet publish (Join-Path $root "app\ColorCorrect.App\ColorCorrect.App.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -p:RestoreLockedMode=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o (Join-Path $dist "app")
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item (Join-Path $root "engine\target\release\ccp-engine.exe") (Join-Path $dist "ccp-engine.exe")
& "$PSScriptRoot\fetch_onnxruntime.ps1" -Dest $dist
New-Item -ItemType Directory -Force -Path (Join-Path $dist "scripts") | Out-Null
Copy-Item (Join-Path $PSScriptRoot "install_libtorch_cuda.ps1") (Join-Path $dist "scripts\install_libtorch_cuda.ps1") -Force
Copy-Item (Join-Path $PSScriptRoot "runtime_hashes.psd1") (Join-Path $dist "scripts\runtime_hashes.psd1") -Force
New-Item -ItemType Directory -Force -Path (Join-Path $dist "models\opt2") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $dist "models\plane") | Out-Null
Copy-Item (Join-Path $root "models\opt2\best_assemble_fp16_1280.torchscript") (Join-Path $dist "models\opt2\")
Copy-Item (Join-Path $root "models\opt2\best_assemble_fp16_1536.torchscript") (Join-Path $dist "models\opt2\")
Copy-Item (Join-Path $root "models\opt2\best_assemble_fp32_1280.onnx") (Join-Path $dist "models\opt2\")
Copy-Item (Join-Path $root "models\opt2\best_assemble_fp32_1536.onnx") (Join-Path $dist "models\opt2\")
New-Item -ItemType Directory -Force -Path (Join-Path $dist "models\apply") | Out-Null
Copy-Item (Join-Path $root "models\apply\lut3d_trilinear.onnx") (Join-Path $dist "models\apply\") -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root "models\apply\lut3d_trilinear.onnx.data") (Join-Path $dist "models\apply\")
Copy-Item (Join-Path $root "models\plane\plane_det_model_YOLO_512_n.onnx") (Join-Path $dist "models\plane\")
Copy-Item (Join-Path $root "models\manifest.json") (Join-Path $dist "models\manifest.json")
Copy-Item (Join-Path $root "refs") (Join-Path $dist "refs") -Recurse

# Keep headless entry points and offline tutorials in every distribution.
foreach ($folder in @('api', 'docs', 'notebooks', 'test_images', 'licenses')) {
    $source = Join-Path $root $folder
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Missing documentation payload: $source" }
    Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/](__pycache__|\.ipynb_checkpoints|\.venv)[\\/]' -and $_.Extension -ne '.pyc'
    } | ForEach-Object {
        $destination = Join-Path (Join-Path $dist $folder) ([IO.Path]::GetRelativePath($source, $_.FullName))
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination
    }
}
Copy-Item -LiteralPath (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $dist

$bundledFf = Join-Path $root "installer\ffmpeg.exe"
$bundledProbe = Join-Path $root "installer\ffprobe.exe"
& "$PSScriptRoot\fetch_ffmpeg.ps1"
if (-not (Test-Path $bundledFf) -or -not (Test-Path $bundledProbe)) {
    throw "Video sidecars are incomplete. Both installer\ffmpeg.exe and installer\ffprobe.exe are required."
}
Copy-Item $bundledFf (Join-Path $dist "ffmpeg.exe") -Force
Copy-Item $bundledProbe (Join-Path $dist "ffprobe.exe") -Force
Copy-Item -LiteralPath (Join-Path $root 'dist\third-party\ffmpeg') -Destination (Join-Path $dist 'licenses\ffmpeg') -Recurse -Force
& "$PSScriptRoot\stage_vc_runtime.ps1" -Dest $dist
$sidecarHashes = Get-FileHash $bundledFf, $bundledProbe -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash)  $([IO.Path]::GetFileName($_.Path))" }
[IO.File]::WriteAllLines((Join-Path $dist "ffmpeg-sha256.txt"), $sidecarHashes)

function Test-TorchKind([string]$dir, [string]$kind) {
    $json = Join-Path $dir "libtorch.json"
    if (-not (Test-Path $json)) { return $false }
    try {
        $info = Get-Content $json -Raw | ConvertFrom-Json
        return ($info.kind -eq $kind -and $info.version -eq "2.9.1")
    } catch {
        return $false
    }
}

$cpuStage = Join-Path $root "dist\libtorch-cpu"
if (-not (Test-TorchKind $cpuStage "cpu")) {
    Write-Host "Staging CPU LibTorch to $cpuStage (does not replace the live libtorch_root)."
    throw 'CPU runtime stage is missing. Fetch SDK/stage without -SkipFetch.'
}

# The staged folder also serves as a genuinely portable build. Keep the CPU
# runtime beside run.cmd. Setup also deploys CPU app-locally, preserving existing
# per-user CPU/CUDA trees and all user settings/session data.
Copy-Item $cpuStage (Join-Path $dist "libtorch") -Recurse -Force

$runCmd = @"
@echo off
set "HERE=%~dp0"
set "CCP_NEW_UI_ROOT=%HERE%"
set "CCP_ENGINE_EXE=%HERE%ccp-engine.exe"
if not defined CCP_LIBTORCH_ROOT if exist "%LOCALAPPDATA%\CCP-New\libtorch-cuda\lib\torch_cuda.dll" set "CCP_LIBTORCH_ROOT=%LOCALAPPDATA%\CCP-New\libtorch-cuda"
if not defined CCP_LIBTORCH_ROOT if exist "%HERE%libtorch\libtorch.json" set "CCP_LIBTORCH_ROOT=%HERE%libtorch"
if not defined CCP_LIBTORCH_ROOT set "CCP_LIBTORCH_ROOT=%LOCALAPPDATA%\CCP-New\libtorch"
set "LIBTORCH=%CCP_LIBTORCH_ROOT%"
set "LIBTORCH_PATH=%CCP_LIBTORCH_ROOT%"
set "KMP_DUPLICATE_LIB_OK=TRUE"
if exist "%HERE%ffmpeg.exe" set "CCP_FFMPEG=%HERE%ffmpeg.exe"
start "" /D "%HERE%" "%HERE%app\ColorCorrect.App.exe" %*
"@
$utf8 = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText((Join-Path $dist "run.cmd"), $runCmd.Replace("`n", "`r`n"), $utf8)

& "$PSScriptRoot\write_build_info.ps1" -Dest $dist
& "$PSScriptRoot\stage_licenses.ps1" -Dest $dist

# Do not hash/package upstream debug symbols that Inno intentionally excludes.
Get-ChildItem -LiteralPath $dist -Recurse -File -Filter '*.pdb' | ForEach-Object {
    if (-not $_.FullName.StartsWith($expectedDist + [IO.Path]::DirectorySeparatorChar)) { throw 'Unexpected symbol path.' }
    Remove-Item -LiteralPath $_.FullName -Force
}

$manifest = Get-ChildItem $dist -Recurse -File |
    Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($dist, $_.FullName).Replace('\', '/')
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
[IO.File]::WriteAllLines((Join-Path $dist "SHA256SUMS.txt"), $manifest, $utf8)

if (-not $SkipPortableArchive) {
    $portableZip = Join-Path (Split-Path $dist -Parent) "ColorCorrect-Studio-win-x64-portable.zip"
    if (Test-Path $portableZip) { Remove-Item -LiteralPath $portableZip -Force }
    Compress-Archive -LiteralPath $dist -DestinationPath $portableZip -CompressionLevel Optimal
    $portableHash = (Get-FileHash $portableZip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$portableZip.sha256", "$portableHash  $([IO.Path]::GetFileName($portableZip))`r`n", $utf8)
    Write-Host "Portable archive at $portableZip"
}

Write-Host "App payload at $dist"
Write-Host "CPU LibTorch staged at $cpuStage. CUDA is not packed; Setup/app reuse or download it."
Write-Host "ONNX Runtime DirectML is beside ccp-engine for Intel/AMD/NVIDIA plane crop."
Write-Host "Portable launch: $dist\run.cmd prefers the per-user CUDA add-on when installed, then bundled CPU. CCP_LIBTORCH_ROOT overrides discovery."
Write-Host "Compile installer/app.iss with Inno Setup after the CPU stage exists."
Write-Host "Unsigned build: SmartScreen will warn until you sign."

if ($BuildInstaller) {
    $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    if (-not $iscc) {
        $isccCandidates = @(
            (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
            (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
            (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
        )
        $iscc = $isccCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    }
    if (-not $iscc) { throw "Inno Setup 6 was not found. Install it or omit -BuildInstaller." }
    & $iscc (Join-Path $root "installer\app.iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed" }
    $setup = Join-Path $root "dist\ColorCorrect-Studio-win-x64.exe"
    $setupHash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$setup.sha256", "$setupHash  $([IO.Path]::GetFileName($setup))`r`n", $utf8)
    Write-Host "Installer at $setup"
}

if ($CudaAddon) {
    Write-Host "CUDA add-on: compile installer/cuda_addon.iss after staging dist/libtorch-cuda, or let app.iss download on demand."
}
} finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
}
