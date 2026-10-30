<#
.SYNOPSIS
  Builds the per-user Windows installer artifacts/PrivatePaste-<version>-win-x64.msi.
.DESCRIPTION
  Works in Windows PowerShell 5.1 and PowerShell 7. Needs the .NET SDK 10; the WiX Toolset is a .NET
  local tool (.config/dotnet-tools.json) and its extensions are pinned below, so nothing is installed system-wide.
  Runs publish-windows.ps1 first unless -NoPublish is given, in which case artifacts/win-x64/PrivatePaste.exe is reused.

  Version rule: the MSI ProductVersion must be three numbers (major.minor.build, at most 255.255.65535).
  A prerelease or build suffix is dropped for the MSI version only: 0.2.0-rc1 -> 0.2.0. The file name keeps
  the full version (PrivatePaste-0.2.0-rc1-win-x64.msi). Because the release candidate and the final release
  share an MSI version, the installer allows same-version upgrades, so installing 0.2.0 over 0.2.0-rc1 replaces it.
.PARAMETER Version
  The release version without the leading "desktop-v", e.g. 0.1.0 or 0.2.0-rc1.
.PARAMETER NoPublish
  Reuse artifacts/win-x64/PrivatePaste.exe instead of running publish-windows.ps1.
#>
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [switch]$NoPublish
)
$ErrorActionPreference = 'Stop'

# Keep in step with the wix version in .config/dotnet-tools.json.
$wixExtensions = @('WixToolset.Util.wixext/6.0.2', 'WixToolset.UI.wixext/6.0.2')

$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'artifacts/win-x64/PrivatePaste.exe'
$icon = Join-Path $repo 'src/PrivatePaste.Desktop/Assets/app.ico'
$wxs = Join-Path $repo 'installer/PrivatePaste.wxs'
$uiWxs = Join-Path $repo 'installer/PrivatePasteUI.wxs'
# Dialog and banner artwork (made by make-installer-art.ps1, committed).
$art = Join-Path $repo 'installer/art'
$msi = Join-Path $repo "artifacts/PrivatePaste-$Version-win-x64.msi"

if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)([-+].*)?$') {
  throw "Version '$Version' is not major.minor.patch with an optional -prerelease or +build suffix"
}
if ([int]$Matches[1] -gt 255 -or [int]$Matches[2] -gt 255 -or [int]$Matches[3] -gt 65535) {
  throw "Version '$Version' is outside the MSI limits 255.255.65535"
}
$msiVersion = '{0}.{1}.{2}' -f [int]$Matches[1], [int]$Matches[2], [int]$Matches[3]

if (-not $NoPublish) {
  & (Join-Path $PSScriptRoot 'publish-windows.ps1') -Version $Version
}
if (-not (Test-Path $exe)) { throw "$exe not found; run without -NoPublish" }

Push-Location $repo
try {
  dotnet tool restore
  if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed ($LASTEXITCODE)" }
  # Downloads the pinned extensions into the per-user WiX cache (%USERPROFILE%\.wix) if they are missing.
  dotnet tool run wix extension add -g @wixExtensions
  if ($LASTEXITCODE -ne 0) { throw "wix extension add failed ($LASTEXITCODE)" }

  if (Test-Path $msi) { Remove-Item $msi -Force }
  $extArgs = $wixExtensions | ForEach-Object { '-ext', $_ }
  dotnet tool run wix build $wxs $uiWxs -arch x64 -culture en-US @extArgs `
    -d "Version=$msiVersion" -d "ExePath=$exe" -d "IconPath=$icon" -d "ArtDir=$art" -o $msi
  if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }
}
finally { Pop-Location }

# wix build leaves a .wixpdb (debug symbols) next to the MSI; it is not shipped.
$pdb = [IO.Path]::ChangeExtension($msi, '.wixpdb')
if (Test-Path $pdb) { Remove-Item $pdb -Force }

Write-Host ("msi: {0} ({1:N1} MB, MSI version {2})" -f $msi, ((Get-Item $msi).Length / 1MB), $msiVersion)
