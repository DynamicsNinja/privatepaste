<#
.SYNOPSIS
  Rebuilds the Windows installer artwork: installer/art/dialog.bmp and installer/art/banner.bmp.
.DESCRIPTION
  The pictures are drawn in installer/art/art.html (Night vault colors, logo and wordmark from branding/).
  This script screenshots them with Playwright (make-installer-art.mjs, using the installed Edge or Playwright's
  Chromium from scripts/readme/node_modules) and saves them as 24-bit BMPs, the format Windows Installer needs.
  The BMPs are committed, so building the MSI (build-installer.ps1, CI) needs neither Node nor a browser.

  Run it after changing art.html, the logo, the lockup or the brand colors, then look at both BMPs and commit them:
    powershell -File scripts/make-installer-art.ps1
  Needs Node.js and, once, `npm ci` in scripts/readme (also used for the README images).
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Split-Path -Parent $PSScriptRoot
$art = Join-Path $repo 'installer/art'
if (-not (Test-Path (Join-Path $PSScriptRoot 'readme/node_modules/playwright'))) {
  throw "Playwright is missing: run 'npm ci' in scripts/readme first"
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("pp-installer-art-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
  node (Join-Path $PSScriptRoot 'make-installer-art.mjs') $tmp
  if ($LASTEXITCODE -ne 0) { throw "make-installer-art.mjs failed ($LASTEXITCODE)" }

  foreach ($name in 'dialog', 'banner') {
    $png = [Drawing.Image]::FromFile((Join-Path $tmp "$name.png"))
    try {
      # The screenshot is 32-bit; MSI bitmaps are plain 24-bit RGB.
      $bmp = New-Object Drawing.Bitmap $png.Width, $png.Height, ([Drawing.Imaging.PixelFormat]::Format24bppRgb)
      $g = [Drawing.Graphics]::FromImage($bmp)
      $g.DrawImage($png, 0, 0, $png.Width, $png.Height)
      $g.Dispose()
      $target = Join-Path $art "$name.bmp"
      $bmp.Save($target, [Drawing.Imaging.ImageFormat]::Bmp)
      $bmp.Dispose()
      Write-Host ("{0}: {1}x{2}" -f $target, $png.Width, $png.Height)
    }
    finally { $png.Dispose() }
  }
}
finally { Remove-Item $tmp -Recurse -Force }
