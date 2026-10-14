# Immutable Gyan 9.0 full build; separate processes, not linked libav.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$hashes = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'runtime_hashes.psd1')
$cache = Join-Path $projectRoot '.build-cache/downloads'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$archive = Join-Path $cache 'ffmpeg-9.0-full_build.zip'
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri 'https://github.com/GyanD/codexffmpeg/releases/download/9.0/ffmpeg-9.0-full_build.zip' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive).Hash -ne $hashes['ffmpeg-win-full-9.0']) { throw 'FFmpeg archive hash mismatch.' }
$extract = Join-Path $projectRoot '.build-cache/ffmpeg-9.0'
if (-not (Test-Path -LiteralPath $extract)) { Expand-Archive -LiteralPath $archive -DestinationPath $extract }
$payload = Join-Path $extract 'ffmpeg-9.0-full_build'
foreach ($name in @('ffmpeg','ffprobe')) {
    $source = Join-Path $payload "bin/$name.exe"
    if ((Get-FileHash -LiteralPath $source).Hash -ne $hashes["$name-win-9.0"]) { throw "Unexpected $name binary." }
    Copy-Item -LiteralPath $source -Destination (Join-Path $projectRoot "installer/$name.exe") -Force
}
$noticeDir = Join-Path $projectRoot 'dist/third-party/ffmpeg'
New-Item -ItemType Directory -Path $noticeDir -Force | Out-Null
foreach ($name in @('LICENSE','README.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $name))) { throw "Missing FFmpeg notice: $name" }
    Copy-Item -LiteralPath (Join-Path $payload $name) -Destination $noticeDir -Force
}
Write-Host 'Verified FFmpeg 9.0 full sidecars and preserved upstream notices.'
