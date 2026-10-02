<#
  Signs an installer for the app's updater: writes <installer>.sig beside it.

    pwsh ./installer/sign-update.ps1 -File artifacts/Notch-Setup-0.10.0.exe

  The private key comes from the UPDATE_SIGNING_KEY environment variable (see new-update-key.ps1).
  Without it nothing is written, and the script says so; a release without a .sig is only accepted
  by apps whose UpdateSigning.PublicKey is still empty.
#>
param(
    [Parameter(Mandatory)][string]$File
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this with pwsh (PowerShell 7).' }

if (-not $env:UPDATE_SIGNING_KEY) {
    Write-Host 'UPDATE_SIGNING_KEY is not set; the installer is not signed for the updater.'
    return
}

$key = [System.Security.Cryptography.ECDsa]::Create()
$bytesRead = 0
$key.ImportPkcs8PrivateKey([Convert]::FromBase64String($env:UPDATE_SIGNING_KEY.Trim()), [ref]$bytesRead)

$stream = [System.IO.File]::OpenRead((Resolve-Path $File))
try {
    # The same call the app verifies with: ECDSA P-256 over SHA-256, signature as r then s (64 bytes).
    $signature = $key.SignData($stream, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
} finally { $stream.Dispose() }

$output = "$File.sig"
[System.IO.File]::WriteAllText($output, [Convert]::ToBase64String($signature))
Write-Host "Signed $File -> $output"
