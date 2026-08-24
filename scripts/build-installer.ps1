#Requires -Version 5.1
<#
.SYNOPSIS
  Build Windows installer (Inno Setup) and portable ZIP for Hexoer.

.DESCRIPTION
  1. Publishes self-contained win-x64 app
  2. Creates portable ZIP under artifacts/portable
  3. If Inno Setup (ISCC) is installed, builds Hexoer-Setup-x.y.z.exe
  4. Otherwise creates a PowerShell soft installer under artifacts/installer

.EXAMPLE
  .\scripts\build-installer.ps1
  .\scripts\build-installer.ps1 -SingleFile
  .\scripts\build-installer.ps1 -Version 1.1.4
#>
param(
    [switch]$SingleFile,
    [switch]$SkipPublish,
    [string]$Version = "1.1.4"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$PublishDir = Join-Path $Root "artifacts\publish\win-x64"
$PortableDir = Join-Path $Root "artifacts\portable"
$InstallerDir = Join-Path $Root "artifacts\installer"
$Iss = Join-Path $Root "installer\Hexoer.iss"
$SoftTemplate = Join-Path $PSScriptRoot "Install-Hexoer.template.ps1"

function Find-Iscc {
    $candidates = @(
        "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Hexoer Windows packaging  v$Version" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if (-not $SkipPublish) {
    $publishScript = Join-Path $PSScriptRoot "publish-windows.ps1"
    if ($SingleFile) {
        & $publishScript -Configuration Release -Runtime win-x64 -Version $Version -SingleFile
    } else {
        & $publishScript -Configuration Release -Runtime win-x64 -Version $Version
    }
}

if (-not (Test-Path (Join-Path $PublishDir "Hexoer.exe"))) {
    throw "Publish output missing: $PublishDir\Hexoer.exe"
}

New-Item -ItemType Directory -Force -Path $PortableDir | Out-Null
New-Item -ItemType Directory -Force -Path $InstallerDir | Out-Null

# Portable ZIP
$zipName = "Hexoer-$Version-win-x64-portable.zip"
$zipPath = Join-Path $PortableDir $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Write-Host "==> Creating portable ZIP: $zipPath" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "    OK" -ForegroundColor Green

# Inno Setup or soft installer
$iscc = Find-Iscc
$setupPath = $null
if ($iscc) {
    Write-Host "==> Building Inno Setup installer with: $iscc" -ForegroundColor Cyan
    $issContent = Get-Content $Iss -Raw -Encoding UTF8
    $issContent = $issContent -replace '#define MyAppVersion "[\d\.]+"', "#define MyAppVersion `"$Version`""
    $publishAbs = (Resolve-Path $PublishDir).Path.Replace('\', '\\')
    $outAbs = (Resolve-Path $InstallerDir).Path.Replace('\', '\\')
    $header = "#define PublishDir `"$publishAbs`"`r`n#define OutputDir `"$outAbs`"`r`n`r`n"
    $tempIss = Join-Path $env:TEMP "Hexoer-$Version.iss"
    Set-Content -Path $tempIss -Value ($header + $issContent) -Encoding UTF8
    & $iscc $tempIss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
    $setupPath = Get-ChildItem $InstallerDir -Filter "Hexoer-Setup-*.exe" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($setupPath) {
        Write-Host "    Installer: $($setupPath.FullName)" -ForegroundColor Green
    }
} else {
    Write-Host "==> Inno Setup (ISCC) not found - generating soft installer" -ForegroundColor Yellow
    Write-Host "    Install Inno Setup 6 from https://jrsoftware.org/isinfo.php for Setup.exe" -ForegroundColor Yellow

    $softInstaller = Join-Path $InstallerDir "Install-Hexoer.ps1"
    $soft = Get-Content $SoftTemplate -Raw -Encoding UTF8
    $soft = $soft.Replace("__VERSION__", $Version).Replace("__ZIPNAME__", $zipName)
    Set-Content -Path $softInstaller -Value $soft -Encoding UTF8
    Copy-Item $zipPath (Join-Path $InstallerDir $zipName) -Force
    Write-Host "    Soft installer: $softInstaller" -ForegroundColor Green
}

Write-Host ""
Write-Host "Artifacts:" -ForegroundColor Cyan
Write-Host "  Publish : $PublishDir"
Write-Host "  Portable: $zipPath"
if ($setupPath) {
    Write-Host "  Setup   : $($setupPath.FullName)"
} else {
    Write-Host "  Soft    : $(Join-Path $InstallerDir 'Install-Hexoer.ps1')"
}
Write-Host "Done." -ForegroundColor Green
