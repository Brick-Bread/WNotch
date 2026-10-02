<#
  Signs programs with a code signing certificate (Authenticode), so Windows can tell who made them.

    pwsh ./installer/sign-authenticode.ps1 -Path artifacts/publish

  Reads the certificate from two environment variables, as repository secrets:
    CODESIGN_PFX_BASE64    the .pfx file, base64 encoded
    CODESIGN_PFX_PASSWORD  its password
  Without them nothing is signed and the script says so. Signs every .exe and .dll that is Notch's own
  (not the .NET runtime, which Microsoft has already signed).

  This does not sign Notch-Setup-*.exe itself: setup carries the app appended to the end of the file
  (src/Notch.Setup/Payload.cs), and a signature written after that would no longer be at the end.
  Signing the installer needs the app embedded in it instead; until then Windows SmartScreen may
  still warn about the download, while the installed programs are signed.
#>
param(
    [Parameter(Mandatory)][string]$Path,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

if (-not $env:CODESIGN_PFX_BASE64) {
    Write-Host 'CODESIGN_PFX_BASE64 is not set; the programs are not signed.'
    return
}

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe was not found (it comes with the Windows SDK).' }

$pfx = Join-Path ([System.IO.Path]::GetTempPath()) ("codesign-" + [guid]::NewGuid().ToString('N') + '.pfx')
try {
    [System.IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:CODESIGN_PFX_BASE64))

    $files = Get-ChildItem $Path -Recurse -Include 'Notch*.exe', 'notchctl.exe', 'Notch*.dll' |
        Where-Object { $_.Name -notmatch '^(Microsoft|System|WindowsBase|PresentationCore|PresentationFramework)' }
    foreach ($file in $files) {
        & $signtool.FullName sign /fd SHA256 /f $pfx /p $env:CODESIGN_PFX_PASSWORD /tr $TimestampUrl /td SHA256 $file.FullName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "signtool failed on $($file.Name)" }
    }
    Write-Host "Signed $($files.Count) file(s)."
} finally {
    Remove-Item $pfx -Force -ErrorAction SilentlyContinue
}
