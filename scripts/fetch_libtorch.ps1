# Fetch an isolated, complete build SDK and stage runtime DLLs. Never modifies
# installed CPU/CUDA trees. CCP_LIBTORCH_SDK may select an existing complete SDK.
param(
    [ValidateSet('cpu','cuda')][string]$Kind = 'cpu',
    [string]$Version = '2.9.1',
    [string]$Cuda = 'cu126',
    [switch]$StageOnly,
    [switch]$ForceDownload
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$cacheRoot = Join-Path $projectRoot '.build-cache'
$hashes = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'runtime_hashes.psd1')
$hashKey = if ($Kind -eq 'cpu') { "libtorch-win-cpu-$Version" } else { "libtorch-win-cuda-$Version-$Cuda" }
$expected = $hashes[$hashKey]
if (-not $expected) { throw "No reviewed SHA-256 for $hashKey" }
New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
$sdk = $env:CCP_LIBTORCH_SDK
if (-not $sdk) {
    $sdkParent = Join-Path $cacheRoot "sdk/$hashKey"
    $sdk = Join-Path $sdkParent 'libtorch'
    if (-not (Test-Path -LiteralPath (Join-Path $sdk 'include/torch/csrc/api/include/torch/torch.h'))) {
        $downloads = Join-Path $cacheRoot 'downloads'
        New-Item -ItemType Directory -Path $downloads -Force | Out-Null
        $archive = Join-Path $downloads "$hashKey.zip"
        if ($ForceDownload -or -not (Test-Path -LiteralPath $archive)) {
            $url = if ($Kind -eq 'cpu') {
                "https://download.pytorch.org/libtorch/cpu/libtorch-win-shared-with-deps-$Version%2Bcpu.zip"
            } else {
                "https://download.pytorch.org/libtorch/$Cuda/libtorch-win-shared-with-deps-$Version%2B$Cuda.zip"
            }
            Invoke-WebRequest -Uri $url -OutFile $archive
        }
        if ((Get-FileHash -LiteralPath $archive).Hash -ne $expected) { throw "SHA-256 mismatch: $archive" }
        if (Test-Path -LiteralPath $sdkParent) { throw "Incomplete SDK at $sdkParent; move it aside and retry." }
        Expand-Archive -LiteralPath $archive -DestinationPath $sdkParent
    }
}
foreach ($required in @('include/torch/csrc/api/include/torch/torch.h','lib/torch.lib','lib/torch.dll','build-version')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sdk $required))) { throw "Incomplete LibTorch SDK: $required at $sdk" }
}
if ((Get-Content -LiteralPath (Join-Path $sdk 'build-version') -Raw).Trim() -notmatch '^2\.9\.1') { throw 'Build SDK must be LibTorch 2.9.1.' }
if ($Kind -eq 'cuda' -and -not (Test-Path -LiteralPath (Join-Path $sdk 'lib/torch_cuda.dll'))) { throw 'CUDA SDK required.' }
if ($Kind -eq 'cpu' -and (Test-Path -LiteralPath (Join-Path $sdk 'lib/torch_cuda.dll'))) { throw 'CPU SDK required for portable release builds.' }
$stage = [IO.Path]::GetFullPath((Join-Path $projectRoot "dist/libtorch-$Kind"))
if (-not $stage.StartsWith([IO.Path]::GetFullPath((Join-Path $projectRoot 'dist')) + [IO.Path]::DirectorySeparatorChar)) { throw 'Unexpected staging path.' }
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $stage 'lib') -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $sdk 'lib') -File -Filter '*.dll' | Copy-Item -Destination (Join-Path $stage 'lib')
foreach ($name in @('build-version','build-hash')) {
    if (Test-Path -LiteralPath (Join-Path $sdk $name)) { Copy-Item -LiteralPath (Join-Path $sdk $name) -Destination $stage }
}
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $stage 'libtorch.json'), (@{kind=$Kind;version=$Version} | ConvertTo-Json -Compress), $utf8)
[IO.File]::WriteAllText((Join-Path $cacheRoot "sdk-$Kind.json"), (@{path=[IO.Path]::GetFullPath($sdk);version=$Version;kind=$Kind} | ConvertTo-Json), $utf8)
Write-Host "Complete build SDK: $sdk"
Write-Host "Runtime-only stage: $stage; installed runtimes were not changed."
