<#
.SYNOPSIS
    Build shellpilot. Stops any running shellpilot.exe first.
.PARAMETER Configuration
    Build configuration (Debug or Release). Default: Debug.
.PARAMETER Publish
    Run dotnet publish instead of build (single-file output to ./dist).
.EXAMPLE
    .\Build.ps1
.EXAMPLE
    .\Build.ps1 -Publish
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ProjectRoot = $PSScriptRoot
$ProjectFile = Join-Path $ProjectRoot 'shellpilot.csproj'

Write-Host '=== shellpilot Build ===' -ForegroundColor Cyan

# Step 1: Stop running shellpilot processes
Write-Host "`n[1/2] Stopping running shellpilot.exe processes..." -ForegroundColor Yellow
$processes = @(Get-Process -Name 'shellpilot' -ErrorAction Ignore)
if ($processes.Count -gt 0) {
    $processes | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Write-Host "      Stopped $($processes.Count) process(es)." -ForegroundColor Green
} else {
    Write-Host '      No running processes found.' -ForegroundColor DarkGray
}

# Step 2: Build or publish
if ($Publish) {
    Write-Host "`n[2/2] Publishing ($Configuration, single-file)..." -ForegroundColor Yellow
    dotnet publish $ProjectFile -c $Configuration -r win-x64 --no-self-contained `
        -p:PublishSingleFile=true `
        -o (Join-Path $ProjectRoot 'dist')
    if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
    Write-Host "`n=== Published to $ProjectRoot\dist ===" -ForegroundColor Green
} else {
    Write-Host "`n[2/2] Building ($Configuration)..." -ForegroundColor Yellow
    dotnet build $ProjectFile -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
    Write-Host "`n=== Build succeeded ===" -ForegroundColor Green
}
