# 编译安卓手机客户端 APK（ASCII-only，兼容 Windows PowerShell 5.1）
# 用法: powershell -ExecutionPolicy Bypass -File .\build-apk.ps1 [-SdkDir <Android SDK 路径>] [-JdkDir <JDK 17 路径>]
param(
    [string]$SdkDir = "H:\ds-harness\android-sdk",
    [string]$JdkDir = "",
    [switch]$Debug
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\WatchingMobile\WatchingMobile.csproj"
$dist = Join-Path $root "dist\apk"

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

$config = if ($Debug) { "Debug" } else { "Release" }

& dotnet build $proj -c $config --nologo `
    -p:AndroidSdkDirectory="$SdkDir" `
    -p:JavaSdkDirectory="$JdkDir"
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$built = Join-Path $root "src\WatchingMobile\bin\$config\net10.0-android\com.watching.mobile-Signed.apk"
if (-not (Test-Path $built)) { throw "APK not found: $built" }

New-Item -ItemType Directory -Force $dist | Out-Null
$target = Join-Path $dist "WatchingMobile-1.0.0.apk"
Copy-Item $built $target -Force

$mb = [Math]::Round((Get-Item $target).Length / 1MB, 2)
Write-Host ""
Write-Host "== Done ==" -ForegroundColor Green
Write-Host "APK: $target ($mb MB)"
Write-Host ""
Write-Host "Install on the phone (USB debugging enabled):"
Write-Host "  `"$SdkDir\platform-tools\adb.exe`" install -r `"$target`""
Write-Host "Or copy the APK to the phone and open it from the file manager."
