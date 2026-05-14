#requires -Version 7
<#
.SYNOPSIS
    Builds the BastionFlow installer.
.DESCRIPTION
    1. Publishes BastionFlow.App in Release / win-x64 / framework-dependent mode
       to <repo>/publish/.
    2. Locates Inno Setup's ISCC.exe (winget package: JRSoftware.InnoSetup).
    3. Compiles installer/BastionFlow.iss to <repo>/dist/BastionFlow-Setup-<ver>.exe.

    Run from anywhere — paths are derived relative to this script.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repo 'publish'
$distDir = Join-Path $repo 'dist'
$iss = Join-Path $PSScriptRoot 'BastionFlow.iss'

if (-not $SkipPublish) {
    Write-Host "[1/3] Cleaning publish folder..." -ForegroundColor Cyan
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    Write-Host "[2/3] Publishing BastionFlow.App ($Configuration, win-x64, framework-dependent)..." -ForegroundColor Cyan
    & dotnet publish (Join-Path $repo 'src\BastionFlow.App') `
        -c $Configuration `
        -r win-x64 `
        --self-contained false `
        -p:PublishSingleFile=false `
        -p:UseAppHost=true `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
} else {
    Write-Host "[1-2/3] Skipping publish (-SkipPublish)." -ForegroundColor DarkGray
}

# Locate ISCC
$iscc = (Get-Command iscc -ErrorAction SilentlyContinue)?.Source
if (-not $iscc) {
    $candidates = @(
        "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) {
    throw "Inno Setup compiler (ISCC.exe) not found. Install with:`n  winget install JRSoftware.InnoSetup --accept-package-agreements --accept-source-agreements"
}

Write-Host "[3/3] Compiling installer with $iscc..." -ForegroundColor Cyan
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir -Force | Out-Null }
& $iscc $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed (exit $LASTEXITCODE)" }

$produced = Get-ChildItem $distDir -Filter 'BastionFlow-Setup-*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($produced) {
    Write-Host ""
    Write-Host "Installer built: $($produced.FullName)" -ForegroundColor Green
    Write-Host "Size: $([math]::Round($produced.Length / 1MB, 1)) MB"
} else {
    Write-Warning "Build finished but no installer file detected in $distDir."
}
