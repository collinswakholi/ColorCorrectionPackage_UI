param([Parameter(Mandatory=$true)][string]$Runtime,
      [Parameter(Mandatory=$true)][string]$VcRedist,
      [ValidateSet('win-x64','win-arm64')][string]$Rid = 'win-x64',
      [string]$Output, [string]$InnoCompiler, [switch]$FetchNotices)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
cargo build --locked --manifest-path (Join-Path $root 'engine/Cargo.toml') -p ccp_bridge --release
if ($LASTEXITCODE -ne 0) { throw 'Worker build failed' }
if (-not $Output) { $Output = Join-Path $root ('dist/native-' + $Rid + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)) }
$arguments = @((Join-Path $PSScriptRoot 'package_native.py'), '--rid', $Rid, '--worker', (Join-Path $root 'engine/target/release/ccp-engine.exe'), '--ort-runtime', $Runtime, '--vc-redist', $VcRedist, '--output', $Output)
if ($FetchNotices) { $arguments += '--fetch-notices' }
python @arguments
if ($LASTEXITCODE -ne 0) { throw 'Packaging failed' }
if ($InnoCompiler) {
    $payload = (Resolve-Path -LiteralPath (Join-Path $Output 'payload')).Path
    & $InnoCompiler "/DDist=$payload" "/O$Output" (Join-Path $root 'installer/app.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
    Get-ChildItem -LiteralPath $Output -File | Where-Object { $_.Extension -in '.zip','.exe' } |
      ForEach-Object { "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" } |
      Set-Content -LiteralPath (Join-Path $Output 'SHA256SUMS') -Encoding ascii
} else {
    Write-Host 'Portable package built. To compile Setup, rerun with -InnoCompiler pointing to a reviewed Inno Setup 6 ISCC.exe.'
}
