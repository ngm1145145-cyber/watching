# Build the WatchingMobile APK (ASCII-only: Windows PowerShell 5.1 mis-parses UTF-8 without BOM).
# Usage: powershell -ExecutionPolicy Bypass -File .\build-apk.ps1 [-SdkDir <Android SDK path>] [-JdkDir <JDK 17 path>]
param(
    [string]$SdkDir = "H:\ds-harness\android-sdk",
    [string]$JdkDir = "",
    [switch]$DebugBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\WatchingMobile\WatchingMobile.csproj"
$dist = Join-Path $root "dist\apk"
$config = if ($DebugBuild) { "Debug" } else { "Release" }

if (-not (Test-Path (Join-Path $SdkDir "platforms"))) {
    throw "Android SDK not found at $SdkDir. Install it first, or pass -SdkDir."
}

if ([string]::IsNullOrWhiteSpace($JdkDir)) {
    if ($env:JAVA_HOME) { $JdkDir = $env:JAVA_HOME }
    else { throw "JDK 17 not found. Set JAVA_HOME or pass -JdkDir." }
}

Write-Host "== Building WatchingMobile APK ==" -ForegroundColor Cyan
Write-Host "Android SDK : $SdkDir"
Write-Host "JDK         : $JdkDir"
Write-Host "Config      : $config"

# IMPORTANT: use "dotnet publish", not "dotnet build".
# Incremental "build" can skip APK packaging (it only rebuilds the managed dll),
# which leaves a stale apk in dist/. So we delete old apks first and publish.
# Also wipe obj/ so that changed Android resources (icons, layouts, manifest)
# are always re-processed by aapt2 - stale resource caches caused a broken
# launcher icon once already.
$apkDir = Join-Path $root "src\WatchingMobile\bin\$config\net10.0-android"
$objDir = Join-Path $root "src\WatchingMobile\obj\$config"
if (Test-Path $objDir) { Remove-Item $objDir -Recurse -Force -ErrorAction SilentlyContinue }
if (Test-Path $apkDir) {
    Get-ChildItem $apkDir -Filter *.apk -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}

& dotnet publish $proj -c $config -f net10.0-android --nologo `
    -p:AndroidSdkDirectory="$SdkDir" `
    -p:JavaSdkDirectory="$JdkDir"
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$built = Join-Path $apkDir "com.watching.mobile-Signed.apk"
if (-not (Test-Path $built)) { throw "APK not found: $built" }

New-Item -ItemType Directory -Force $dist | Out-Null
$target = Join-Path $dist "WatchingMobile-1.0.2.apk"
Copy-Item $built $target -Force

$mb = [Math]::Round((Get-Item $target).Length / 1MB, 2)
Write-Host ""
Write-Host "== Done ==" -ForegroundColor Green
Write-Host "APK: $target ($mb MB)"
Write-Host "Built: $((Get-Item $target).LastWriteTime)"
Write-Host ""
Write-Host "Install on the phone (USB debugging enabled):"
Write-Host "  `"$SdkDir\platform-tools\adb.exe`" install -r `"$target`""
Write-Host "Or copy the APK to the phone and open it from the file manager."
