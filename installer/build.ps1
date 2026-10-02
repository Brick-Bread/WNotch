<#
  Builds the installer: artifacts\Notch-Setup-<Version>.exe

  The installer is src\Notch.Setup (a small .NET Framework WPF program) with the published app,
  zipped, appended to it. See src\Notch.Setup\Payload.cs for the layout.

    .\installer\build.ps1 -Version 0.9.0
    .\installer\build.ps1 -Version 0.9.0 -SkipPublish   # reuse artifacts\publish from an earlier run
#>
param(
    [string]$Version = '0.0.0-dev',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$stub = Join-Path $artifacts 'setup-stub'
$zipPath = Join-Path $artifacts 'payload.zip'
$output = Join-Path $artifacts "Notch-Setup-$Version.exe"

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

if (-not $SkipPublish) {
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    # Both executables are published self-contained into one folder so they share a single
    # copy of the .NET runtime and users need nothing preinstalled.
    Invoke-Dotnet publish (Join-Path $root 'src\Notch.App') -c Release -r win-x64 --self-contained true -o $publish "-p:Version=$Version"
    Invoke-Dotnet publish (Join-Path $root 'src\Notch.Hook') -c Release -r win-x64 --self-contained true -o $publish "-p:Version=$Version"
}
Get-ChildItem $publish -Filter *.pdb -Recurse | Remove-Item -Force

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

if (Test-Path $stub) { Remove-Item $stub -Recurse -Force }
Invoke-Dotnet build (Join-Path $root 'src\Notch.Setup') -c Release -o $stub "-p:Version=$Version"

# [setup program][zip][zip length: int64][magic]
Copy-Item (Join-Path $stub 'Notch.Setup.exe') $output -Force
$zipLength = (Get-Item $zipPath).Length
$out = [System.IO.File]::Open($output, 'Append', 'Write')
try {
    $in = [System.IO.File]::OpenRead($zipPath)
    try { $in.CopyTo($out) } finally { $in.Dispose() }
    $out.Write([BitConverter]::GetBytes([int64]$zipLength), 0, 8)
    $magic = [System.Text.Encoding]::ASCII.GetBytes('NOTCHPAY')
    $out.Write($magic, 0, $magic.Length)
} finally { $out.Dispose() }

Remove-Item $zipPath -Force
Write-Host "Built $output ($([math]::Round((Get-Item $output).Length / 1MB, 1)) MB)"
