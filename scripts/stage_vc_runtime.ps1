# App-local CRT deployment from the licensed MSVC build tools used to build Rust.
# No system installation, administrator rights, or copying System32 binaries.
param([Parameter(Mandatory=$true)][string]$Dest)
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'MSVC build tools/vswhere are required to stage the app-local VC++ runtime.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'No licensed MSVC x64 build tool installation was found.' }
$redist = Get-ChildItem -LiteralPath (Join-Path $installation 'VC/Redist/MSVC') -Directory |
    Where-Object Name -Match '^\d+\.\d+\.\d+' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$crt = Join-Path $redist.FullName 'x64/Microsoft.VC143.CRT'
if (-not (Test-Path -LiteralPath $crt)) { throw "Missing x64 app-local CRT: $crt" }
foreach ($required in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $crt $required))) { throw "Missing CRT component: $required" }
}
foreach ($target in @($Dest,(Join-Path $Dest 'app'))) {
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Get-ChildItem -LiteralPath $crt -File -Filter '*.dll' | Copy-Item -Destination $target -Force
}
$inventory = Get-ChildItem -LiteralPath $crt -File -Filter '*.dll' | ForEach-Object {
    @{name=$_.Name;version=$_.VersionInfo.FileVersion;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
}
$noticeDir = Join-Path $Dest 'licenses/msvc'
New-Item -ItemType Directory -Path $noticeDir -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $noticeDir 'runtime-inventory.json'), ($inventory | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
Write-Host "Staged app-local MSVC CRT $($redist.Name), with no system changes."
