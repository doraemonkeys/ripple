<#
.SYNOPSIS
    Release-publish splash (NativeAOT) and deploy to npm/dist.
.DESCRIPTION
    Stops any running splash.exe, runs `dotnet publish -c Release` (NativeAOT
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
$ProjectFile = Join-Path $ProjectRoot 'splash.csproj'
$DistDir = Join-Path $ProjectRoot 'dist'
$NpmDistDir = Join-Path $ProjectRoot 'npm\dist'

Write-Host '=== splash Release Publish ===' -ForegroundColor Cyan

Write-Host "`n[1/3] Stopping running splash.exe processes..." -ForegroundColor Yellow
$processes = @(Get-Process -Name 'splash' -ErrorAction Ignore)
if ($processes.Count -gt 0) {
    $processes | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Write-Host "      Stopped $($processes.Count) process(es)." -ForegroundColor Green
} else {
    Write-Host '      No running processes found.' -ForegroundColor DarkGray
}

Write-Host "`n[2/3] Publishing (Release, NativeAOT)..." -ForegroundColor Yellow
dotnet publish $ProjectFile -c Release -r win-x64 -o $DistDir
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

Write-Host "`n[3/3] Deploying to npm/dist..." -ForegroundColor Yellow
$src = Join-Path $DistDir 'splash.exe'
$dst = Join-Path $NpmDistDir 'splash.exe'
if (-not (Test-Path $src)) { throw "Published binary not found: $src" }
New-Item -ItemType Directory -Force -Path $NpmDistDir | Out-Null
Copy-Item $src $dst -Force
$size = [Math]::Round((Get-Item $dst).Length / 1MB, 2)
Write-Host "      Copied to $dst ($size MB)" -ForegroundColor Green

Write-Host "`n=== Done ===" -ForegroundColor Green
