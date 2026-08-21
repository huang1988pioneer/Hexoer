#Requires -Version 5.1
<#
.SYNOPSIS
  Publish Hexoer as a self-contained Windows x64 build.
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SingleFile,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root "src\Hexoer\Hexoer.csproj"
$OutDir = Join-Path $Root "artifacts\publish\$Runtime"

Write-Host "==> Publishing Hexoer ($Configuration / $Runtime)" -ForegroundColor Cyan
if (Test-Path $OutDir) {
    Remove-Item $OutDir -Recurse -Force
}

$args = @(
    "publish", $Project,
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", $OutDir,
    "/p:PublishReadyToRun=true",
    "/p:IncludeNativeLibrariesForSelfExtract=true",
    "/p:DebugType=none",
    "/p:DebugSymbols=false",
    "/p:CopyOutputSymbolsToPublishDirectory=false"
)

if ($FrameworkDependent) {
    $args += @("--self-contained", "false")
} else {
    $args += @("--self-contained", "true")
}

if ($SingleFile) {
    $args += @(
        "/p:PublishSingleFile=true",
        "/p:EnableCompressionInSingleFile=true"
    )
} else {
    $args += @("/p:PublishSingleFile=false")
}

& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Native Skia/HarfBuzz packages sometimes still drop large .pdb files
Get-ChildItem $OutDir -Recurse -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $OutDir -Recurse -Filter *.xml -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'Avalonia|Markdig|CommunityToolkit|Skia|HarfBuzz' } |
    Remove-Item -Force -ErrorAction SilentlyContinue

$exe = Join-Path $OutDir "Hexoer.exe"
if (-not (Test-Path $exe)) { throw "Hexoer.exe not found in $OutDir" }

$sizeMb = [math]::Round((Get-ChildItem $OutDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "==> Published to $OutDir ($sizeMb MB)" -ForegroundColor Cyan
Write-Output $OutDir
