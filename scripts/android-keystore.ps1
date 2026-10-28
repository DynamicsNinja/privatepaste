<#
.SYNOPSIS
  Generates the Android release keystore and prints the GitHub secret commands.
.DESCRIPTION
  Creates android/keystore/release.jks (RSA 4096, valid 10000 days, alias "privatepaste") with a random password,
  prints the password once, and prints the `gh secret set` commands to run. Refuses to overwrite a keystore.
  -Output is for testing the script itself; leave it out for the real keystore.
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\android\keystore\release.jks')
)

$ErrorActionPreference = 'Stop'

$alias = 'privatepaste'
$Output = [IO.Path]::GetFullPath($Output)

if (Test-Path -LiteralPath $Output) {
    throw "$Output already exists. Refusing to overwrite it. Delete it yourself only if you are sure it is not in use."
}

$keytool = if ($env:JAVA_HOME -and (Test-Path (Join-Path $env:JAVA_HOME 'bin/keytool*'))) {
    Join-Path $env:JAVA_HOME 'bin/keytool'
} else {
    'keytool'
}

# 32 random bytes as base64 url-safe text (no quotes or spaces, so it is safe in a shell).
$bytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes)
$rng.Dispose()
$password = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Output) | Out-Null

$env:PP_KS_PASS = $password
try {
& $keytool -genkeypair -v `
    -keystore $Output -storetype PKCS12 `
    -alias $alias -keyalg RSA -keysize 4096 -validity 10000 `
    -storepass:env PP_KS_PASS -keypass:env PP_KS_PASS `
    -dname 'CN=PrivatePaste, O=ficdev, C=HR'
} finally {
    Remove-Item Env:PP_KS_PASS -ErrorAction SilentlyContinue
}
if ($LASTEXITCODE -ne 0) {
    if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Force }
    throw "keytool failed with exit code $LASTEXITCODE."
}

Write-Host ''
Write-Host "Keystore: $Output"
Write-Host "Alias:    $alias"
Write-Host "Password: $password   (shown once; store it in your password manager)"
Write-Host ''
Write-Host 'Run these in the repository, one at a time (each one stores a GitHub Actions secret):'
Write-Host ''
Write-Host "[Convert]::ToBase64String([IO.File]::ReadAllBytes('$Output')) | gh secret set ANDROID_KEYSTORE_BASE64"
Write-Host "'$password' | gh secret set ANDROID_KEYSTORE_PASSWORD"
Write-Host "'$alias' | gh secret set ANDROID_KEY_ALIAS"
Write-Host "'$password' | gh secret set ANDROID_KEY_PASSWORD"
Write-Host ''
Write-Host "Back this file up. Without it, updates can't be installed over the existing app."
