# Compatibility entry point: samples are now tracked files, never junctions.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
foreach ($name in @('Blank.JPG','ColorCard_Big.JPG','ColorCard_Combo.JPG','Sample_3572.JPG','Sample_3573.JPG')) {
    $sample = Join-Path $root "test_images\$name"
    if (-not (Test-Path -LiteralPath $sample -PathType Leaf)) { throw "Missing public sample: $name" }
    if ((Get-Item -LiteralPath $sample).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Sample must not be a junction: $name" }
}
Write-Host 'Five public sample files available; no links or sibling projects required.'
