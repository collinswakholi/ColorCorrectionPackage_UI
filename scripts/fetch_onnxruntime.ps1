# Immutable NuGet runtime artifacts, isolated from installed developer libraries.
param([string]$Dest = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\win-cpu'), [string]$Version='1.22.1')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $root '.build-cache\nuget-runtime'
$hashes = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'runtime_hashes.psd1')
New-Item -ItemType Directory -Force -Path $cache,$Dest | Out-Null
foreach ($item in @(@{id='Microsoft.ML.OnnxRuntime.DirectML';version=$Version;native='runtimes\win-x64\native'}, @{id='Microsoft.AI.DirectML';version='1.15.4';native='bin\x64-win'})) {
    $key = "$($item.id).$($item.version)"
    $expected = $hashes[$key]
    if (-not $expected) { throw "No reviewed SHA-256 for $key" }
    $archive = Join-Path $cache "$key.zip"
    if (-not (Test-Path -LiteralPath $archive)) {
        $lower = $item.id.ToLowerInvariant()
        Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$lower/$($item.version)/$lower.$($item.version).nupkg" -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive).Hash -ne $expected) { throw "Checksum mismatch: $key" }
    $extracted = Join-Path $cache $key
    if (-not (Test-Path -LiteralPath $extracted)) { Expand-Archive -LiteralPath $archive -DestinationPath $extracted }
    if ($item.id -eq 'Microsoft.ML.OnnxRuntime.DirectML') {
        Get-ChildItem (Join-Path $extracted $item.native) -File -Filter '*.dll' | Copy-Item -Destination $Dest -Force
    } else {
        $dml = Get-ChildItem $extracted -Recurse -File -Filter DirectML.dll | Where-Object { $_.FullName -match '[\\/]x64-win[\\/]|[\\/]x64[\\/]' } | Select-Object -First 1
        if (-not $dml) { throw 'DirectML x64 runtime missing.' }
        Copy-Item -LiteralPath $dml.FullName -Destination (Join-Path $Dest 'DirectML.dll') -Force
    }
    $noticeDir = Join-Path $Dest "licenses\$key"
    New-Item -ItemType Directory -Force -Path $noticeDir | Out-Null
    Get-ChildItem $extracted -Recurse -File | Where-Object { $_.Name -match '(?i)license|notice|copying|\.nuspec$' } | ForEach-Object {
        $target = Join-Path $noticeDir ([IO.Path]::GetRelativePath($extracted,$_.FullName))
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }
}
foreach ($name in @('onnxruntime.dll','DirectML.dll')) { if (-not (Test-Path (Join-Path $Dest $name))) { throw "Missing runtime $name" } }
Write-Host 'Verified ORT 1.22.1 / DirectML 1.15.4 and retained package licenses.'
