<#
.SYNOPSIS
    Release-publish ripple (NativeAOT) and deploy to npm/dist.
.DESCRIPTION
    Stops any running ripple.exe, runs `dotnet publish -c Release` (NativeAOT
    single native exe into ./dist), and mirrors the resulting binary to
    ./npm/dist so the npm package ships the fresh build.
.EXAMPLE
    .\Build.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ProjectRoot = $PSScriptRoot
$ProjectFile = Join-Path $ProjectRoot 'ripple.csproj'
$DistDir = Join-Path $ProjectRoot 'dist'
$NpmDistDir = Join-Path $ProjectRoot 'npm\dist'

Write-Host '=== ripple Release Publish ===' -ForegroundColor Cyan

Write-Host "`n[1/3] Stopping running ripple.exe processes..." -ForegroundColor Yellow
$processes = @(Get-Process -Name 'ripple' -ErrorAction Ignore)
if ($processes.Count -gt 0) {
    $processes | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Write-Host "      Stopped $($processes.Count) process(es)." -ForegroundColor Green
} else {
    Write-Host '      No running processes found.' -ForegroundColor DarkGray
}

Write-Host "`n[2/3] Publishing (Release, NativeAOT)..." -ForegroundColor Yellow
# NativeAOT's ILCompiler targets invoke vswhere.exe to locate MSVC link.exe;
# vswhere lives at a stable Program Files location but isn't on PATH for
# plain pwsh sessions (only Developer PowerShell adds it). Prepend the
# installer dir ourselves when the caller hasn't so `.\Build.ps1` works
# from any shell on a box with VS installed, not only from a VS-opened
# prompt. No-op when vswhere is already resolvable.
$vswhereDir = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
if (-not (Get-Command vswhere.exe -ErrorAction Ignore) -and
    (Test-Path (Join-Path $vswhereDir 'vswhere.exe') -PathType Leaf)) {
    $env:PATH = "$vswhereDir;$env:PATH"
}
dotnet publish $ProjectFile -c Release -r win-x64 -o $DistDir
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

Write-Host "`n[3/3] Deploying to npm/dist..." -ForegroundColor Yellow
$src = Join-Path $DistDir 'ripple.exe'
$dst = Join-Path $NpmDistDir 'ripple.exe'
if (-not (Test-Path $src)) { throw "Published binary not found: $src" }
New-Item -ItemType Directory -Force -Path $NpmDistDir | Out-Null
Copy-Item $src $dst -Force
$size = [Math]::Round((Get-Item $dst).Length / 1MB, 2)
Write-Host "      Copied to $dst ($size MB)" -ForegroundColor Green

Write-Host "`n=== Done ===" -ForegroundColor Green
