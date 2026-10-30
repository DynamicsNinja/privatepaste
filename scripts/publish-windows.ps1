<#
.SYNOPSIS
  Builds the unsigned single-file win-x64 PrivatePaste.exe and zips it (CI signs the exe and re-zips it).
.DESCRIPTION
  Works in Windows PowerShell 5.1 and PowerShell 7. Needs the .NET SDK 10.
  Output: artifacts/win-x64/PrivatePaste.exe and artifacts/PrivatePaste-win-x64.zip
.PARAMETER Version
  Assembly/file version, default 0.0.0 (CI passes the tag without the leading "desktop-v").
#>
param([string]$Version = '0.0.0')
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $repo 'artifacts/win-x64'
$zip = Join-Path $repo 'artifacts/PrivatePaste-win-x64.zip'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

# No "+<commit>" suffix on the product version: code signing (.signpath/) only signs an exe whose product version is
# exactly the release version.
dotnet publish (Join-Path $repo 'src/PrivatePaste.Desktop') -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
  -p:Version=$Version -p:IncludeSourceRevisionInInformationalVersion=false -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Native symbol files shipped by SkiaSharp/HarfBuzz are not part of the app.
Get-ChildItem $out -Filter *.pdb | Remove-Item -Force

$exe = Join-Path $out 'PrivatePaste.exe'
if (-not (Test-Path $exe)) { throw "PrivatePaste.exe was not produced" }

Compress-Archive -Path $exe -DestinationPath $zip -CompressionLevel Optimal
Write-Host ("exe: {0:N1} MB  zip: {1:N1} MB" -f ((Get-Item $exe).Length / 1MB), ((Get-Item $zip).Length / 1MB))
