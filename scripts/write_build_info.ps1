param([Parameter(Mandatory=$true)][string]$Dest)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$commit = (git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Build provenance requires a Git checkout.' }
$dirty = [bool](git -C $root status --porcelain)
$sdk = (dotnet --version).Trim()
$rust = (rustc --version).Trim()
if ($sdk -ne '10.0.402' -or $rust -notmatch '^rustc 1\.95\.0 ') { throw 'Pinned toolchain mismatch.' }
$metadata = cargo metadata --manifest-path (Join-Path $root 'engine\Cargo.toml') --locked --format-version 1 | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Dependency inventory failed.' }
$inventory = [ordered]@{
    rust = @($metadata.packages | Sort-Object name,version | ForEach-Object { [ordered]@{ name=$_.name; version=$_.version; license=$_.license; source=$_.source; repository=$_.repository } })
    nuget = @{}
    runtime_artifacts = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'runtime_hashes.psd1')
}
Get-ChildItem (Join-Path $root 'app') -Filter packages.lock.json -Recurse -File | ForEach-Object {
    $inventory.nuget[$_.Directory.Name] = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
}
$info = [ordered]@{
    product='ColorCorrect Studio'; version='0.1.0'; preview=$true; signed=$false
    source_commit=$commit; dirty_worktree=$dirty
    release_eligible=(-not $dirty -and $env:CCP_RELEASE_BUILD -eq '1')
    build_purpose=$(if ($env:CCP_RELEASE_BUILD -eq '1') { 'reviewed-main-draft' } else { 'validation-only' })
    built_utc=[DateTime]::UtcNow.ToString('o'); target='win-x64'; dotnet_sdk=$sdk; rust=$rust
    still_images_only=$true; libtorch='2.9.1-cpu'; onnxruntime='1.22.1'; directml='1.15.4'
    model_manifest=Get-Content (Join-Path $root 'models\manifest.json') -Raw | ConvertFrom-Json
}
New-Item -ItemType Directory -Force -Path (Join-Path $Dest 'licenses') | Out-Null
$info | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $Dest 'build-info.json') -Encoding utf8
$inventory | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath (Join-Path $Dest 'licenses\dependency-inventory.json') -Encoding utf8
