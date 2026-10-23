param([Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This helper installs a build tool only on an explicitly dispatched GitHub Actions runner.' }
$null = New-Item -ItemType Directory -Path $Output -Force
$installer = Join-Path $Output 'innosetup-6.7.3.exe'
Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $installer
$expected = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $expected) { throw 'Inno compiler download checksum mismatch' }
$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B.V.') { throw 'Inno compiler signature mismatch' }
$destination = Join-Path $Output 'compiler'
$process = Start-Process -FilePath $installer -ArgumentList @('/PORTABLE=1','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER','/NOICONS','/TASKS=',("/DIR=`"$destination`"")) -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Inno compiler setup failed: $($process.ExitCode)" }
"CCP_INNO_COMPILER=$(Join-Path $destination 'ISCC.exe')" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
