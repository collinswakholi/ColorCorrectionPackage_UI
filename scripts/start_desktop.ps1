# Build with a complete isolated SDK; run with a runtime-only distribution.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& "$PSScriptRoot\link_samples.ps1"
& "$PSScriptRoot\fetch_libtorch.ps1" -Kind cpu -StageOnly
$sdk = (Get-Content (Join-Path $root '.build-cache\sdk-cpu.json') -Raw | ConvertFrom-Json).path
$saved = @{}
foreach ($name in @('LIBTORCH','LIBTORCH_PATH','LIBTORCH_BYPASS_VERSION_CHECK','PATH')) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:LIBTORCH = $sdk
    $env:LIBTORCH_PATH = $sdk
    $env:LIBTORCH_BYPASS_VERSION_CHECK = '1'
    $env:PATH = (Join-Path $sdk 'lib') + ';' + $env:PATH
    cargo build --manifest-path (Join-Path $root 'engine\Cargo.toml') --locked -p ccp_bridge --release --features tch,ort-plane,video
    if ($LASTEXITCODE -ne 0) { throw 'cargo build failed' }
} finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
$env:CCP_ENGINE_EXE = Join-Path $root 'engine\target\release\ccp-engine.exe'
$env:CCP_NEW_UI_ROOT = $root
if (-not $env:CCP_LIBTORCH_ROOT) { $env:CCP_LIBTORCH_ROOT = Join-Path $root 'dist\libtorch-cpu' }
$env:KMP_DUPLICATE_LIB_OK = 'TRUE'
if (Test-Path (Join-Path $root 'installer\ffmpeg.exe')) { $env:CCP_FFMPEG = Join-Path $root 'installer\ffmpeg.exe' }
dotnet run --project (Join-Path $root 'app\ColorCorrect.App\ColorCorrect.App.csproj')
if ($LASTEXITCODE -ne 0) { throw 'desktop launch failed' }
