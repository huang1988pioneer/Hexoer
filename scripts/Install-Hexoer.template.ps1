#Requires -Version 5.1
<#
  Hexoer soft installer (no Inno Setup required)
  Installs portable build to %LocalAppData%\Programs\Hexoer and creates Start Menu / Desktop shortcuts.
#>
param(
    [string]$InstallDir = "$env:LocalAppData\Programs\Hexoer",
    [switch]$NoDesktopShortcut
)

$ErrorActionPreference = "Stop"
$Version = "__VERSION__"
$ZipName = "__ZIPNAME__"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$SourceZip = Join-Path $here $ZipName
if (-not (Test-Path $SourceZip)) {
    $SourceZip = Join-Path (Join-Path $here "..\portable") $ZipName
}
if (-not (Test-Path $SourceZip)) {
    throw "Cannot find portable package: $ZipName"
}

Write-Host "Installing Hexoer $Version to $InstallDir" -ForegroundColor Cyan
if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Expand-Archive -Path $SourceZip -DestinationPath $InstallDir -Force

$exe = Join-Path $InstallDir "Hexoer.exe"
if (-not (Test-Path $exe)) {
    throw "Hexoer.exe missing after extract"
}

$shell = New-Object -ComObject WScript.Shell
$startMenu = Join-Path $env:AppData "Microsoft\Windows\Start Menu\Programs\Hexoer"
New-Item -ItemType Directory -Force -Path $startMenu | Out-Null

$lnk = $shell.CreateShortcut((Join-Path $startMenu "Hexoer.lnk"))
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $InstallDir
$lnk.Description = "Hexo desktop toolkit"
$lnk.Save()

if (-not $NoDesktopShortcut) {
    $desk = Join-Path ([Environment]::GetFolderPath("Desktop")) "Hexoer.lnk"
    $lnk2 = $shell.CreateShortcut($desk)
    $lnk2.TargetPath = $exe
    $lnk2.WorkingDirectory = $InstallDir
    $lnk2.Description = "Hexo desktop toolkit"
    $lnk2.Save()
}

$uninstPath = Join-Path $InstallDir "Uninstall-Hexoer.ps1"
$uninst = @"
`$ErrorActionPreference = 'SilentlyContinue'
Remove-Item -LiteralPath '$InstallDir' -Recurse -Force
Remove-Item -LiteralPath '$startMenu' -Recurse -Force
Remove-Item -LiteralPath ([IO.Path]::Combine([Environment]::GetFolderPath('Desktop'), 'Hexoer.lnk')) -Force
Write-Host 'Hexoer uninstalled.' -ForegroundColor Green
"@
Set-Content -Path $uninstPath -Value $uninst -Encoding UTF8

Write-Host "Done. Launching Hexoer..." -ForegroundColor Green
Start-Process $exe
