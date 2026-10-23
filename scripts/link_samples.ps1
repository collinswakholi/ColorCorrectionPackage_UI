# Junction Speedy catalog + oracle stills into CCP_New_UI. Do not copy blobs.
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $PSScriptRoot
$workspace = Split-Path -Parent $here
$candidates = @(
    (Join-Path $workspace "Speedy_CC_build_1\assets\test_images"),
    (Join-Path $workspace "assets\test_images")
)
$src = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $src) {
    Write-Host "No Speedy test_images found. Catalog will stay empty until you link one."
    exit 0
}

function Ensure-Junction([string]$link, [string]$target) {
    if (Test-Path $link) {
        $item = Get-Item $link -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            return
        }
        Write-Host "Skip $link (exists and is not a junction)"
        return
    }
    $parent = Split-Path -Parent $link
    if (-not (Test-Path $parent)) {
        New-Item -ItemType Directory -Path $parent | Out-Null
    }
    New-Item -ItemType Junction -Path $link -Target $target | Out-Null
    Write-Host "Linked $link -> $target"
}

Ensure-Junction (Join-Path $here "assets\test_images") $src
Ensure-Junction (Join-Path $here "testdata\samples") $src
