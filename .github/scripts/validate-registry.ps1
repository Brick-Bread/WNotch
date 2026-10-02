<#
  Checks site/plugins/registry.json, the plugin list on the website.

    .\.github\scripts\validate-registry.ps1               # shape of every entry, then each plugin's latest release
    .\.github\scripts\validate-registry.ps1 -SkipNetwork  # shape only

  Fails (exit 1) listing every problem found. What the app does with the file is in
  src/Notch.Core/Plugins/PluginRegistry.cs; these checks are the same plus the release itself.
#>
param(
    [string]$Path = 'site/plugins/registry.json',
    [switch]$SkipNetwork,
    # Entries that already exist on this branch may keep "verified"; a new entry may not claim it.
    [string]$BaseFile = ''
)

$ErrorActionPreference = 'Stop'
$errors = New-Object System.Collections.Generic.List[string]
function Fail([string]$id, [string]$message) { $errors.Add("${id}: $message") }

try {
    $data = Get-Content $Path -Raw | ConvertFrom-Json
} catch {
    Write-Host "::error::$Path is not valid JSON: $($_.Exception.Message)"
    exit 1
}
if (-not $data.plugins) { Write-Host "::error::$Path has no ""plugins""."; exit 1 }

$known = @{}
if ($BaseFile -and (Test-Path $BaseFile)) {
    foreach ($old in (Get-Content $BaseFile -Raw | ConvertFrom-Json).plugins) { $known[$old.id] = $old }
}

$seen = @{}
$permissionWords = '^[a-z]{1,32}$'
foreach ($p in $data.plugins) {
    $id = [string]$p.id
    if ($id -notmatch '^[a-z0-9]+([.-][a-z0-9]+)*$' -or $id.Length -gt 64) { Fail $id 'the id must be lowercase letters and digits in groups separated by dots or dashes, at most 64 characters.'; continue }
    if ($seen.ContainsKey($id)) { Fail $id 'the id is listed twice.'; continue }
    $seen[$id] = $true
    if (-not $p.name) { Fail $id 'a name is required.' }
    if ([string]$p.repository -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$') { Fail $id 'the repository must be owner/repo on GitHub.'; continue }
    foreach ($word in @($p.permissions)) { if ($word -and ([string]$word).ToLowerInvariant() -notmatch $permissionWords) { Fail $id "the permission '$word' is not a single word." } }
    if (@($p.tags).Count -gt 8) { Fail $id 'at most 8 tags.' }
    if ($p.verified -eq $true -and $BaseFile -and -not $known.ContainsKey($id)) { Fail $id '"verified" is set by the maintainers after reading the code; leave it out of a new entry.' }

    if ($SkipNetwork) { continue }

    # The release must be installable: one .zip, holding a plugin.json with this id.
    $headers = @{ 'User-Agent' = 'Notch-Registry' }
    if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }
    try {
        $release = Invoke-RestMethod "https://api.github.com/repos/$($p.repository)/releases/latest" -Headers $headers
    } catch {
        Fail $id "the latest release of $($p.repository) could not be read (does it have a published release?)."
        continue
    }
    $zips = @($release.assets | Where-Object { $_.name -like '*.zip' })
    if ($zips.Count -ne 1) { Fail $id "the latest release must have exactly one .zip asset; it has $($zips.Count)."; continue }
    if (-not ([string]$zips[0].browser_download_url).StartsWith('https://github.com/')) { Fail $id 'the .zip must be hosted on github.com.'; continue }

    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("registry-" + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory $work | Out-Null
        $zip = Join-Path $work 'plugin.zip'
        Invoke-WebRequest $zips[0].browser_download_url -OutFile $zip -Headers @{ 'User-Agent' = 'Notch-Registry' }
        Expand-Archive $zip -DestinationPath (Join-Path $work 'x')
        $manifest = Get-ChildItem (Join-Path $work 'x') -Filter plugin.json -Recurse -Depth 1 | Select-Object -First 1
        if (-not $manifest) { Fail $id 'no plugin.json at the top of the .zip (or in a single folder in it).'; continue }
        $text = Get-Content $manifest.FullName -Raw
        if ($text -notmatch '"id"\s*:\s*"([^"]+)"') { Fail $id 'plugin.json has no id.'; continue }
        if ($Matches[1] -ne $id) { Fail $id "plugin.json in the release says the id is '$($Matches[1])'." }
    } finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($errors.Count -gt 0) {
    foreach ($e in $errors) { Write-Host "::error file=$Path::$e" }
    exit 1
}
Write-Host "$($seen.Count) plugin(s) in $Path look good."
