param(
    [ValidateSet('recommended','cpu','custom')][string]$Mode = 'recommended',
    [string]$Components = '',
    [switch]$Diagnose, [switch]$Verify, [switch]$Install, [switch]$NonInteractive,
    [ValidateRange(5,600)][int]$TimeoutSeconds = 120
)
# Bootstrap and repair require only Windows PowerShell, never Python or .NET.
$ErrorActionPreference = 'Stop'
$packageRoot = $PSScriptRoot
function Get-PayloadHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
$manifest = Get-Content -LiteralPath (Join-Path $packageRoot 'setup-components.json') -Raw | ConvertFrom-Json
$machine = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
$actual = if ($machine -eq 'ARM64') { 'arm64' } elseif ($machine -eq 'AMD64') { 'x64' } else { 'unsupported' }
if ($actual -ne $manifest.architecture) { throw "This package requires $($manifest.architecture); Windows is $actual." }
$adapters = @(); $inventoryError = $null
try {
    $adapters = @(Get-CimInstance Win32_VideoController -OperationTimeoutSec 10 | ForEach-Object {
        $software = $_.Name -match '(?i)basic render|warp|swiftshader|llvmpipe|lavapipe'
        [ordered]@{name=$_.Name; pnp_id=$_.PNPDeviceID; driver=$_.DriverVersion; status=$_.Status;
                   classification=$(if ($software) {'software'} else {'unverified'});
                   reason='Inventory only; actual execution requires verification'}
    })
} catch { $inventoryError = $_.Exception.Message }
$requestedComponents = @($Components -split ',' | Where-Object {$_})
foreach ($component in $requestedComponents) { if ($component -notin @('webgpu','directml')) { throw "Unknown component: $component" } }
$selected = @('cpu')
if ($Mode -eq 'custom') { $selected += $requestedComponents }
elseif ($Mode -eq 'recommended' -and ($inventoryError -or $adapters.Count -eq 0 -or @($adapters | Where-Object {$_.classification -ne 'software'}).Count -gt 0)) { $selected += @('directml','webgpu') }
$metadata = Get-Content -LiteralPath (Join-Path $packageRoot 'prerequisites/vc-redist.json') -Raw | ConvertFrom-Json
$runtimeKey = "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\$actual"
$vc = Get-ItemProperty -LiteralPath $runtimeKey -ErrorAction SilentlyContinue
$vcReady = $vc -and $vc.Installed -eq 1 -and [version]$vc.Version.TrimStart('v') -ge [version]$metadata.version
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$admin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$volume = Get-PSDrive -Name ([IO.Path]::GetPathRoot($packageRoot).TrimEnd('\').TrimEnd(':')) -ErrorAction SilentlyContinue
$report = [ordered]@{schema=1; mode=$Mode; architecture=$actual; os=[Environment]::OSVersion.VersionString;
    inventory_status=$(if ($inventoryError) {'not_checked'} else {'observed'}); inventory_error=$inventoryError;
    adapters=$adapters; selected_components=$selected; vc_runtime=[ordered]@{required=$metadata.version;ready=[bool]$vcReady};
    administrator=$admin; free_bytes=$(if ($volume) {$volume.Free} else {$null});
    network='not_checked; private runtime payloads are bundled for offline installation';
    correction_verification='Recorded independently in each setup_probe result';
    drivers='System drivers are never changed; use Windows Update or the official hardware vendor'; probes=@()}
function Invoke-Probe([string]$Policy) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = Join-Path $packageRoot 'ccp-engine.exe'
    $info.WorkingDirectory = $packageRoot
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    # Windows PowerShell may inherit UTF-8 with a BOM from the console. NDJSON
    # must start with a JSON character, not EF-BB-BF, including in Sandbox.
    $info.StandardOutputEncoding = New-Object Text.UTF8Encoding $false
    $info.StandardErrorEncoding = New-Object Text.UTF8Encoding $false
    $info.EnvironmentVariables['CCP_NEW_UI_ROOT'] = $packageRoot
    $info.EnvironmentVariables['ORT_DYLIB_PATH'] = Join-Path $packageRoot 'onnxruntime.dll'
    $info.EnvironmentVariables['CCP_INFERENCE_COMPONENTS'] = ($selected -join ',')
    $info.EnvironmentVariables['PATH'] = "$packageRoot;$env:PATH"
    $process = New-Object Diagnostics.Process; $process.StartInfo = $info
    try {
        # .NET Framework (Windows PowerShell 5) has no StandardInputEncoding
        # property. Process.Start captures Console.InputEncoding for its writer.
        $originalInputEncoding = [Console]::InputEncoding
        try {
            [Console]::InputEncoding = New-Object Text.UTF8Encoding $false
            [void]$process.Start()
        } finally { [Console]::InputEncoding = $originalInputEncoding }
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.WriteLine((@{cmd='setup_probe';policy=$Policy;force_cpu=($Policy -eq 'cpu-inference')} | ConvertTo-Json -Compress))
        $process.StandardInput.WriteLine('{"cmd":"quit"}')
        $process.StandardInput.Close()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill(); [void]$process.WaitForExit(5000)
            return @{policy=$Policy;status='failed';reason='Probe timed out'}
        }
        $lines = @($stdout.Result -split "`n" | Where-Object {$_ -match '^\s*\{'} | ForEach-Object { try { $_ | ConvertFrom-Json } catch {} })
        $result = $lines | Where-Object {$null -ne $_.inference -and -not $_.event} | Select-Object -Last 1
        $verified = $process.ExitCode -eq 0 -and $result -and $result.ok -and $result.setup_verified -and $result.inference.warmed -and $result.correction.status -eq 'verified'
        $reason = $null
        if (-not $verified) {
            $errorReply = $lines | Where-Object {$_.error} | Select-Object -Last 1
            if ($process.ExitCode -ne 0) { $reason = "Worker exited with code $($process.ExitCode). Check diagnostics and repair the selected runtime." }
            elseif (-not $result) { $reason = 'Worker returned no setup result.'; if ($errorReply) { $reason += " $($errorReply.error)" } }
            elseif ($result.inference.error) { $reason = $result.inference.error }
            elseif ($result.correction.error) { $reason = $result.correction.error }
            else { $reason = 'The selected engine did not complete inference and correction verification.' }
        }
        return @{policy=$Policy;status=$(if ($verified) {'verified'} else {'failed'});
                 result=$result;reason=$reason;exit_code=$process.ExitCode;diagnostics=$stderr.Result}
    } catch { return @{policy=$Policy;status='failed';reason=$_.Exception.Message} }
    finally {
        # Any exception after Start (including a broken input pipe) must not
        # leave this owned probe worker running. Never wait indefinitely for it.
        try { if (-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) } } catch {}
        $process.Dispose()
    }
}
if ($Install) {
    Write-Host "Setup selection: $Mode. Components: $($selected -join ', '). CPU inference and correction are always included."
    Write-Host "VC runtime ready: $([bool]$vcReady). Missing VC prerequisites require administrator access. Private runtimes are bundled; no driver is installed."
    if (-not $NonInteractive -and (Read-Host 'Type INSTALL to apply this selection') -cne 'INSTALL') { Write-Host 'No setup changes were made.'; exit 0 }
    foreach ($component in $manifest.components | Where-Object {$_.id -in $selected -or $_.required}) {
        foreach ($file in $component.files) {
            $path = Join-Path $packageRoot $file.path
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                if (-not $component.required) {
                    $bundled = if ($component.bundled_source) { Join-Path $packageRoot $component.bundled_source } else { $null }
                    if ($bundled -and (Test-Path -LiteralPath $bundled -PathType Leaf) -and (Get-PayloadHash $bundled) -eq $file.sha256) {
                        try { Copy-Item -LiteralPath $bundled -Destination $path }
                        catch { Write-Warning "Optional $($component.id) could not be installed: $($_.Exception.Message). CPU remains available."; $selected = @($selected | Where-Object {$_ -ne $component.id}); continue }
                        Write-Host "Installed bundled $($component.id) support. Restart the app to use it."
                    } else { Write-Warning "Optional $($component.id) is absent. CPU remains available. Repair using the matching installer or full portable archive."; $selected = @($selected | Where-Object {$_ -ne $component.id}); continue }
                }
                if ($component.required) { throw "Missing required component: $($file.path). Repair using the original installer or portable archive." }
            }
            if ((Get-PayloadHash $path) -ne $file.sha256) {
                if ($component.required) { throw "Required component integrity check failed: $($file.path)" }
                Write-Warning "Optional component integrity check failed: $($file.path). It will not be enabled; CPU remains available."
                $selected = @($selected | Where-Object {$_ -ne $component.id})
            }
        }
    }
    if (-not $vcReady) {
        & (Join-Path $packageRoot 'check-system.ps1') -InstallPrerequisites
        if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        $vc = Get-ItemProperty -LiteralPath $runtimeKey -ErrorAction SilentlyContinue
        $vcReady = $vc -and $vc.Installed -eq 1 -and [version]$vc.Version.TrimStart('v') -ge [version]$metadata.version
        $report.vc_runtime.ready = [bool]$vcReady
    }
    $config = Join-Path $env:LOCALAPPDATA 'ColorCorrectStudio'
    New-Item -ItemType Directory -Force -Path $config | Out-Null
    $state = [ordered]@{schema=1;mode=$Mode;components=$selected;detection_policy=$(if ($Mode -eq 'cpu') {'cpu-inference'} else {'auto'});correction_policy=$(if ($Mode -eq 'cpu') {'cpu'} else {'auto'})}
    $temporary = Join-Path $config ([guid]::NewGuid().ToString() + '.tmp')
    [IO.File]::WriteAllText($temporary, ($state | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
    Move-Item -LiteralPath $temporary -Destination (Join-Path $config 'setup-state.json') -Force
    $report.saved = $true
}
if ($Verify) {
    if ($vcReady) {
        $report.probes = @((@('cpu-inference') + @($selected | Where-Object {$_ -ne 'cpu'})) | ForEach-Object {Invoke-Probe $_})
    } else { $report.probes = @(@{status='not_checked';reason='Install the required VC runtime before worker verification'}) }
}
if ($Install) { [IO.File]::WriteAllText((Join-Path $config 'setup-report.json'), ($report | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding $false)) }
if ($Diagnose -or $Verify) { $report | ConvertTo-Json -Depth 12 }
else {
    Write-Host ($report | ConvertTo-Json -Depth 8)
    if (-not $Install) { Write-Host 'Preview only. Use -Install to apply this selection; -Verify runs isolated CPU/GPU inference checks. No driver is installed.' }
}
