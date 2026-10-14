# Invoked only after exact-main and functional gates in the manual workflow.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$sourceUrl=$env:SOURCE_URL
$sourceHash=$env:SOURCE_SHA256
if (-not $sourceUrl.StartsWith('https://') -or $sourceHash -notmatch '^[0-9a-fA-F]{64}$') { throw 'Reviewed corresponding source HTTPS URL and SHA-256 required.' }
$release=Join-Path $root 'dist\release'
if (Test-Path -LiteralPath $release) { throw 'Release staging already exists; move it aside for an intentional rerun.' }
New-Item -ItemType Directory -Path $release | Out-Null
$source=Join-Path $release 'third-party-source.zip'
Invoke-WebRequest -Uri $sourceUrl -OutFile $source
if ((Get-FileHash -LiteralPath $source).Hash -ne $sourceHash.ToUpperInvariant()) { throw 'Corresponding source checksum mismatch.' }
Copy-Item -LiteralPath (Join-Path $root 'dist\ColorCorrect-Studio-win-x64.exe'),(Join-Path $root 'dist\ColorCorrect-Studio-win-x64.exe.sha256'),(Join-Path $root 'dist\win-cpu\build-info.json'),(Join-Path $root 'dist\win-cpu\licenses\dependency-inventory.json'),(Join-Path $root 'THIRD_PARTY_NOTICES.md'),(Join-Path $root 'docs\release-notes.md') -Destination $release
