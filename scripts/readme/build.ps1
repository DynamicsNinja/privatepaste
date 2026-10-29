# Rebuilds docs/images/hero-dark.png and hero-light.png from the current apps. See README.md in this folder.
#
#   powershell -File scripts/readme/build.ps1
#
# Runs a throwaway local server (never your real one), renders the desktop popup and the Android history from
# demo.json, screenshots the web UI and composes the hero. Needs the .NET SDK, Node.js and a JDK 21 (JAVA_HOME,
# or Android Studio's / Microsoft's JDK, which are found automatically).
param(
    [int]$Port = 5311
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = (Resolve-Path (Join-Path $here '..\..')).Path
$out = Join-Path $here 'out'
$demo = Join-Path $here 'demo.json'
$url = "http://localhost:$Port"

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null

function Invoke-Native([string]$what, [scriptblock]$run) {
    & $run
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

# 1. A throwaway server with a fresh, empty data directory and the dev password.
Write-Host 'Publishing the server...'
Invoke-Native 'Server publish' { dotnet publish (Join-Path $root 'src\PrivatePaste.Server') -c Release -o (Join-Path $out 'server') --nologo -v quiet }
$env:PP_DB_KEY = 'readme-demo-key-0123456789'
$env:PP_PASSWORD = 'dev'
$env:PP_DATA_DIR = Join-Path $out 'data'
$server = Start-Process dotnet -ArgumentList 'PrivatePaste.Server.dll', '--urls', $url `
    -WorkingDirectory (Join-Path $out 'server') -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $out 'server.log') -RedirectStandardError (Join-Path $out 'server.err.log')
Remove-Item Env:PP_DB_KEY, Env:PP_PASSWORD, Env:PP_DATA_DIR

try {
    $deadline = (Get-Date).AddSeconds(60)
    while ($true) {
        try { if ((Invoke-WebRequest "$url/health" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { break } } catch { }
        if ($server.HasExited) { throw "The server exited; see $out\server.err.log" }
        if ((Get-Date) -gt $deadline) { throw "The server did not answer $url/health within 60 s" }
        Start-Sleep -Milliseconds 500
    }

    $env:PP_README_DEMO = $demo
    $env:PP_SCREENSHOT_DIR = $out

    # 2. Desktop popup (Avalonia, headless) -> out/readme-popup-{dark,light}.png
    Write-Host 'Rendering the desktop popup...'
    Invoke-Native 'Desktop render' { dotnet test (Join-Path $root 'tests\PrivatePaste.Tests') --filter 'FullyQualifiedName~Readme_popup' --nologo -v quiet }

    # 3. Android history (Compose, Robolectric) -> out/readme-history-{dark,light}.png
    Write-Host 'Rendering the Android history...'
    if (-not $env:JAVA_HOME) {
        $jdk = @("$env:ProgramFiles\Android\Android Studio\jbr") +
            @(Get-ChildItem "$env:ProgramFiles\Microsoft\jdk-21*" -Directory -ErrorAction SilentlyContinue | ForEach-Object FullName) |
            Where-Object { Test-Path (Join-Path $_ 'bin\java.exe') } | Select-Object -First 1
        if (-not $jdk) { throw 'No JDK found: set JAVA_HOME to a JDK 21' }
        $env:JAVA_HOME = $jdk
    }
    Push-Location (Join-Path $root 'android')
    try { Invoke-Native 'Android render' { .\gradlew.bat -q testDebugUnitTest --tests '*ReadmeScreenshotTest*' } }
    finally { Pop-Location }

    # 4. Seed the server, shoot the web UI, compose the hero -> docs/images/hero-{dark,light}.png
    Write-Host 'Capturing the web UI and composing the hero...'
    Push-Location $here
    try {
        if (-not (Test-Path (Join-Path $here 'node_modules\playwright'))) { Invoke-Native 'npm install' { npm install --no-audit --no-fund --silent } }
        Invoke-Native 'Capture' { node capture.mjs --url $url --password dev }
    }
    finally { Pop-Location }
}
finally {
    Remove-Item Env:PP_README_DEMO, Env:PP_SCREENSHOT_DIR -ErrorAction SilentlyContinue
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
Write-Host "Done. Review docs/images/hero-*.png, then commit them."
