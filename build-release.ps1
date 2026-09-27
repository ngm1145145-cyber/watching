# Build a portable Watching server + desktop client (ASCII-only so Windows PowerShell 5.1 parses it safely).
# Usage: powershell -ExecutionPolicy Bypass -File .\build-release.ps1 [-Runtime win-x64] [-FrameworkDependent]
param(
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\Watching\Watching.csproj"
$flavor = if ($FrameworkDependent) { "framework" } else { "selfcontained" }
$out = Join-Path $root "dist\Watching-$Runtime-$flavor"

Write-Host "== Publishing Watching ($Runtime) ==" -ForegroundColor Cyan

$publishArgs = @(
    "publish", $proj,
    "-c", "Release",
    "-r", $Runtime,
    "-o", $out,
    "--nologo"
)

if ($FrameworkDependent) {
    $publishArgs += @("--self-contained", "false")
    Write-Host "Mode: framework-dependent (small, needs .NET 10 Desktop Runtime on the target PC)"
} else {
    $publishArgs += @("--self-contained", "true", "-p:PublishSingleFile=false")
    Write-Host "Mode: self-contained (no runtime install needed)"
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$shortcuts = Join-Path $out "shortcuts"
New-Item -ItemType Directory -Force $shortcuts | Out-Null
Copy-Item (Join-Path $root "scripts\*") $shortcuts -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "== Done ==" -ForegroundColor Green
Write-Host "Output: $out"
Write-Host ""
Write-Host "Server   (the PC being watched) : $out\Watching.exe --server"
Write-Host "Client   (the PC watching)      : $out\Watching.exe --client --connect <SERVER_IP>"
Write-Host "Phone    (any browser)          : http://<SERVER_IP>:8899/"
Write-Host ""
Write-Host "First run on the server: right-click the tray icon -> Settings"
Write-Host "  1) set a settings password"
Write-Host "  2) optionally require an access password for clients"
Write-Host "  3) only enable remote control if you really need it"
