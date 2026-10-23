# Compatibility entry point for the pinned DirectML/WebGPU runtime bundle.
param([string]$Dest = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\gpu-runtime'), [string]$Rid = 'win-x64')
$ErrorActionPreference = 'Stop'
python (Join-Path $PSScriptRoot 'fetch_gpu_runtime.py') --rid $Rid --output $Dest
if ($LASTEXITCODE -ne 0) { throw 'GPU runtime staging failed' }
