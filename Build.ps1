<#
.SYNOPSIS
    Release-publish ripple (NativeAOT) and deploy to npm/dist.
.DESCRIPTION
    Stops any running ripple.exe, runs `dotnet publish -c Release` (NativeAOT
    single native exe into ./dist), optionally Authenticode-signs the binary,
    and mirrors the resulting binary to ./npm/dist so the npm package ships
    the fresh build.
.PARAMETER Sign
    Authenticode-sign the published binary. Requires -PfxPath and will prompt
    for the PFX password at sign time.
.PARAMETER PfxPath
    Path to the PFX file holding the code-signing certificate.
    Default: C:\MyProj\vault\yotsuda.pfx
.PARAMETER TimestampUrl
    RFC 3161 timestamp authority used to timestamp the signature so it remains
    verifiable after the cert expires.
    Default: http://timestamp.digicert.com
.EXAMPLE
    .\Build.ps1
.EXAMPLE
    .\Build.ps1 -Sign
#>
[CmdletBinding()]
param(
    [switch]$Sign,
    [string]$PfxPath = 'C:\MyProj\vault\yotsuda.pfx',
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ProjectRoot = $PSScriptRoot
$ProjectFile = Join-Path $ProjectRoot 'ripple.csproj'
$DistDir = Join-Path $ProjectRoot 'dist'
$NpmDistDir = Join-Path $ProjectRoot 'npm\dist'

Write-Host '=== ripple Release Publish ===' -ForegroundColor Cyan

Write-Host "`n[1/4] Stopping running ripple.exe processes..." -ForegroundColor Yellow
$processes = @(Get-Process -Name 'ripple' -ErrorAction Ignore)
if ($processes.Count -gt 0) {
    $processes | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Write-Host "      Stopped $($processes.Count) process(es)." -ForegroundColor Green
} else {
    Write-Host '      No running processes found.' -ForegroundColor DarkGray
}

Write-Host "`n[2/4] Publishing (Release, NativeAOT)..." -ForegroundColor Yellow
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

$src = Join-Path $DistDir 'ripple.exe'
if (-not (Test-Path $src)) { throw "Published binary not found: $src" }

if ($Sign) {
    Write-Host "`n[3/4] Authenticode-signing $src ..." -ForegroundColor Yellow
    if (-not (Test-Path $PfxPath)) { throw "PFX not found at $PfxPath" }
    $pfxPassword = Read-Host "      Enter PFX password" -AsSecureString
    $cert = Get-PfxCertificate -FilePath $PfxPath -Password $pfxPassword
    $result = Set-AuthenticodeSignature `
        -FilePath $src `
        -Certificate $cert `
        -HashAlgorithm SHA256 `
        -TimestampServer $TimestampUrl `
        -IncludeChain NotRoot
    if ($result.Status -ne 'Valid') {
        throw "Sign failed for $src : $($result.StatusMessage)"
    }
    Write-Host "      Signed (status: Valid, thumbprint: $($cert.Thumbprint))" -ForegroundColor Green
} else {
    Write-Host "`n[3/4] Skipping signing (pass -Sign to enable, e.g. for publish builds)." -ForegroundColor Gray
}

Write-Host "`n[4/4] Deploying to npm/dist..." -ForegroundColor Yellow
$dst = Join-Path $NpmDistDir 'ripple.exe'
New-Item -ItemType Directory -Force -Path $NpmDistDir | Out-Null
Copy-Item $src $dst -Force
$size = [Math]::Round((Get-Item $dst).Length / 1MB, 2)
Write-Host "      Copied to $dst ($size MB)" -ForegroundColor Green

Write-Host "`n=== Done ===" -ForegroundColor Green
