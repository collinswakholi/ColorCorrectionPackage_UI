# Invoked only after exact-main and functional gates in the manual workflow.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$release=Join-Path $root 'dist\release'
if (Test-Path -LiteralPath $release) { throw 'Release staging already exists; move it aside for an intentional rerun.' }
New-Item -ItemType Directory -Path $release | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'dist\ColorCorrect-Studio-win-x64.exe'),(Join-Path $root 'dist\ColorCorrect-Studio-win-x64.exe.sha256'),(Join-Path $root 'dist\win-cpu\build-info.json'),(Join-Path $root 'dist\win-cpu\licenses\dependency-inventory.json'),(Join-Path $root 'THIRD_PARTY_NOTICES.md'),(Join-Path $root 'docs\release-notes.md') -Destination $release
Copy-Item -LiteralPath (Join-Path $root 'docs\model-provenance.md'),(Join-Path $root 'models\NOTICE.md'),(Join-Path $root 'models\LICENSE-AGPL-3.0.txt') -Destination $release
