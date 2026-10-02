<#
  Makes the key pair that signs Notch updates. Run it once, by the maintainer, on a trusted computer.

    pwsh ./installer/new-update-key.ps1

  It prints two lines:
    PUBLIC  paste into UpdateSigning.PublicKey in src/Notch.Core/Updates/UpdateSigning.cs, commit, release.
    PRIVATE add to the repository's secrets as UPDATE_SIGNING_KEY. Never commit it, never share it.

  From the first release built with both in place, the app refuses any update whose installer does
  not carry a signature made with the private key. Releases published before then stay installable by hand.
  Losing the private key means shipping a new public key in a release that people install by hand;
  keep a copy somewhere safe.

  Needs PowerShell 7 (pwsh): Windows PowerShell 5.1 lacks the ECDSA calls used here.
#>
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this with pwsh (PowerShell 7).' }

$key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::NamedCurves.nistP256)
$public = [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())
$private = [Convert]::ToBase64String($key.ExportPkcs8PrivateKey())

Write-Host "PUBLIC  $public"
Write-Host "PRIVATE $private"
Write-Host ''
Write-Host 'Add the PRIVATE value as the repository secret UPDATE_SIGNING_KEY, and paste the PUBLIC value into UpdateSigning.PublicKey.'
