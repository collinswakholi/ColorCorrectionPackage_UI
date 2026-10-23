param([Parameter(Mandatory=$true)][string]$Path)
$ErrorActionPreference = 'Stop'
$signature = Get-AuthenticodeSignature -LiteralPath $Path
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation(?:,|$)') {
    throw 'The VC prerequisite must have a valid Microsoft signature.'
}
$version = (Get-Item -LiteralPath $Path).VersionInfo.FileVersion
@{version=$version; signature='Valid'; signer=$signature.SignerCertificate.Subject; sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()} | ConvertTo-Json -Compress
