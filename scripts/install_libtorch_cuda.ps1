# Stage official LibTorch 2.9.x CUDA *runtime DLLs* into one libtorch_root.
# Does not ship inside the app installer. Reuses a local tree or cache zip
# when present; otherwise downloads from download.pytorch.org.
param(
    [string]$Dest = $(Join-Path $env:LOCALAPPDATA "CCP-New\libtorch-cuda"),
    [string]$ReusePath = "",
    [string]$Zip = "",
    [string]$Version = "2.9.1",
    [string]$Cuda = "cu126"
)

$ErrorActionPreference = "Stop"
$hashManifest = Join-Path $PSScriptRoot "runtime_hashes.psd1"
if (-not (Test-Path $hashManifest)) { throw "Missing runtime_hashes.psd1 beside the CUDA installer script." }
$hashes = Import-PowerShellDataFile $hashManifest
$cache = Join-Path $env:LOCALAPPDATA "CCP-New\cache"
New-Item -ItemType Directory -Force -Path $cache | Out-Null

function Test-CudaTree([string]$dir) {
    if (-not $dir) { return $false }
    $dll = Join-Path $dir "lib\torch_cuda.dll"
    $json = Join-Path $dir "libtorch.json"
    if (-not (Test-Path $dll)) { return $false }
    if (Test-Path $json) {
        try {
            $meta = Get-Content $json -Raw | ConvertFrom-Json
            if ($meta.kind -and ($meta.kind -ne "cuda")) { return $false }
            if ($meta.version -and ($meta.version -notmatch '^2\.9')) { return $false }
        } catch {
            return $false
        }
    }
    return $true
}

function Stage-RuntimeDlls([string]$src, [string]$dest) {
    # Never destroy an active CPU/custom runtime or a partial installation.
    if (Test-Path -LiteralPath $dest) { throw "Destination already exists and is not a valid CUDA runtime: $dest. Choose a new destination." }
    New-Item -ItemType Directory -Force -Path (Join-Path $dest "lib") | Out-Null
    foreach ($name in @("libtorch.json", "build-hash", "build-version")) {
        $p = Join-Path $src $name
        if (Test-Path $p) { Copy-Item $p $dest -Force }
    }
    Copy-Item (Join-Path $src "lib\*.dll") (Join-Path $dest "lib") -Force
    if (-not (Test-Path (Join-Path $dest "libtorch.json"))) {
        $utf8 = New-Object System.Text.UTF8Encoding $false
        [System.IO.File]::WriteAllText((Join-Path $dest "libtorch.json"), "{`"kind`":`"cuda`",`"version`":`"$Version`"}", $utf8)
    }
}

if (Test-CudaTree $Dest) {
    Write-Host "Already have CUDA LibTorch at $Dest"
    exit 0
}

if ($ReusePath -and (Test-CudaTree $ReusePath)) {
    Write-Host "Reusing $ReusePath"
    Stage-RuntimeDlls $ReusePath $Dest
    Write-Host "Wrote $Dest"
    exit 0
}

$cachedZip = Join-Path $cache "libtorch-win-cuda-$Version.zip"
if (-not $Zip) { $Zip = $cachedZip }
if (-not (Test-Path $Zip) -or ((Get-Item $Zip).Length -lt 1MB)) {
    $url = "https://download.pytorch.org/libtorch/$Cuda/libtorch-win-shared-with-deps-$Version%2B$Cuda.zip"
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $Zip -UseBasicParsing
}
$hashKey = "libtorch-win-cuda-$Version-$Cuda"
$expectedHash = $hashes[$hashKey]
if (-not $expectedHash) { throw "No reviewed SHA-256 is pinned for $hashKey." }
$actualHash = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash
if ($actualHash -ne $expectedHash) { throw "CUDA LibTorch archive hash mismatch. Expected $expectedHash, got $actualHash." }

$extract = Join-Path $cache ("extract-cuda-" + [Guid]::NewGuid().ToString('N'))
Write-Host "Extracting $Zip"
Expand-Archive -Path $Zip -DestinationPath $extract -Force
$payload = Join-Path $extract "libtorch"
if (-not (Test-Path $payload)) {
    $payload = (Get-ChildItem $extract -Directory | Select-Object -First 1).FullName
}
if (-not (Test-Path (Join-Path $payload "lib\torch_cuda.dll"))) {
    throw "CUDA zip did not contain lib\torch_cuda.dll"
}
Stage-RuntimeDlls $payload $Dest
Write-Host "Wrote $Dest (runtime DLLs only)"
