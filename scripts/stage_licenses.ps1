param([Parameter(Mandatory=$true)][string]$Dest)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$licenses = Join-Path $Dest 'licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
$dotnetSdk = Split-Path -Parent (Get-Command dotnet).Source
$dotnetNotices = Join-Path $licenses 'dotnet'
New-Item -ItemType Directory -Force -Path $dotnetNotices | Out-Null
foreach($name in @('LICENSE.txt','ThirdPartyNotices.txt')) {
    $path = Join-Path $dotnetSdk $name
    if (-not (Test-Path -LiteralPath $path)) { throw "Pinned .NET SDK notice missing: $name" }
    Copy-Item -LiteralPath $path -Destination $dotnetNotices -Force
}
$torchDir = Join-Path $licenses 'pytorch-2.9.1'
New-Item -ItemType Directory -Force -Path $torchDir | Out-Null
# Full PyTorch LICENSE includes embedded third-party notices; preserve verbatim.
$cached = Join-Path $root '.build-cache\pytorch-2.9.1-LICENSE.txt'
if (-not (Test-Path -LiteralPath $cached)) { Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/pytorch/pytorch/v2.9.1/LICENSE' -OutFile $cached }
Copy-Item -LiteralPath $cached -Destination (Join-Path $torchDir 'LICENSE.txt') -Force
$metadata = cargo metadata --manifest-path (Join-Path $root 'engine\Cargo.toml') --locked --format-version 1 | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'cargo metadata failed' }
foreach ($package in $metadata.packages | Where-Object { $_.source }) {
    $folder = Split-Path -Parent $package.manifest_path
    $target = Join-Path $licenses "rust\$($package.name)-$($package.version)"
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Name -match '(?i)^(license|notice|copying)' } | Copy-Item -Destination $target -Force
}
Get-ChildItem (Join-Path $root 'app') -Recurse -Filter packages.lock.json -File | ForEach-Object {
    $lock = Get-Content $_.FullName -Raw | ConvertFrom-Json
    foreach($framework in $lock.dependencies.PSObject.Properties) {
        foreach($dependency in $framework.Value.PSObject.Properties) {
            if (-not $dependency.Value.resolved) { continue }
            $nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
            $folder = Join-Path $nuget "$($dependency.Name.ToLowerInvariant())\$($dependency.Value.resolved)"
            if (-not (Test-Path -LiteralPath $folder)) { continue }
            $target = Join-Path $licenses "nuget\$($dependency.Name)-$($dependency.Value.resolved)"
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            Get-ChildItem -LiteralPath $folder -File -Recurse | Where-Object { $_.Name -match '(?i)^(license|notice|copying|ofl)' -or $_.Extension -eq '.nuspec' } | ForEach-Object {
                $to = Join-Path $target ([IO.Path]::GetRelativePath($folder,$_.FullName))
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $to -Force
            }
        }
    }
}
