#requires -Version 7
<#
.SYNOPSIS
    Builds the BastionFlow multi-architecture installer.
.DESCRIPTION
    1. Publishes BastionFlow.App for BOTH win-x64 and win-arm64 (framework-
       dependent) into <repo>/publish-x64/ and <repo>/publish-arm64/.
    2. Locates Inno Setup's ISCC.exe (winget package: JRSoftware.InnoSetup).
    3. Compiles installer/BastionFlow.iss into a single installer that detects
       the runtime architecture and installs the matching binaries.

    Run from anywhere — paths are derived relative to this script.
.PARAMETER SkipPublish
    Skip the dotnet publish step (useful for iterating on the .iss).
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$publishX64   = Join-Path $repo 'publish-x64'
$publishArm64 = Join-Path $repo 'publish-arm64'
$distDir = Join-Path $repo 'dist'
$iss = Join-Path $PSScriptRoot 'BastionFlow.iss'

function Publish-Arch {
    param([string]$Runtime, [string]$OutDir)
    Write-Host "  -> $Runtime to $OutDir" -ForegroundColor DarkGray
    if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
    & dotnet publish (Join-Path $repo 'src\BastionFlow.App') `
        -c $Configuration `
        -r $Runtime `
        --self-contained false `
        -p:PublishSingleFile=false `
        -p:UseAppHost=true `
        -o $OutDir | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Runtime (exit $LASTEXITCODE)" }
}

if (-not $SkipPublish) {
    Write-Host "[1/3] Publishing both architectures..." -ForegroundColor Cyan
    Publish-Arch -Runtime 'win-x64'   -OutDir $publishX64
    Publish-Arch -Runtime 'win-arm64' -OutDir $publishArm64
} else {
    Write-Host "[1/3] Skipping publish (-SkipPublish)." -ForegroundColor DarkGray
    foreach ($dir in @($publishX64, $publishArm64)) {
        if (-not (Test-Path $dir)) {
            throw "Required publish folder missing: $dir. Re-run without -SkipPublish."
        }
    }
}

$x64Bin = (Get-ChildItem $publishX64 -File -Recurse | Measure-Object Length -Sum).Sum
$armBin = (Get-ChildItem $publishArm64 -File -Recurse | Measure-Object Length -Sum).Sum
Write-Host ("[2/3] Publish output: x64 {0:N1} MB, arm64 {1:N1} MB" -f ($x64Bin/1MB), ($armBin/1MB)) -ForegroundColor Cyan

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
    Write-Host "Size: $([math]::Round($produced.Length / 1MB, 1)) MB (multi-arch: x64 + arm64)"

    # Also drop a copy into the workspace-level dist folder (one above the repo).
    # The repo's own dist/ is gitignored; the workspace dist is OneDrive-synced
    # so the installer is immediately reachable from any of Ioannis's PCs.
    $workspaceDist = Split-Path -Parent $repo
    $workspaceDist = Join-Path $workspaceDist 'dist'
    if (-not (Test-Path $workspaceDist)) { New-Item -ItemType Directory -Path $workspaceDist -Force | Out-Null }
    $copyTarget = Join-Path $workspaceDist $produced.Name
    Copy-Item -Path $produced.FullName -Destination $copyTarget -Force
    Write-Host "Also copied to workspace dist: $copyTarget" -ForegroundColor DarkGray
} else {
    Write-Warning "Build finished but no installer file detected in $distDir."
}
