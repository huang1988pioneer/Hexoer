#Requires -Version 5.1
<#
.SYNOPSIS
  Publish Hexoer release artifacts and optionally upload them to GitHub Releases.

.DESCRIPTION
  1) dotnet publish -> self-contained Windows build
  2) Copy a single Hexoer.exe when single-file publishing is requested
  3) Build portable ZIP and installer through scripts/build-installer.ps1
  4) Generate SHA256 checksums for release assets
  5) Optionally create or update a GitHub release with gh

.EXAMPLE
  .\scripts\publish.ps1 -Version 1.1.4 -Runtime win-x64
  .\scripts\publish.ps1 -Version 1.1.4 -SingleFile -CreateGitHubRelease
#>
param(
    [string]$Version = "1.1.4",
    [string]$Runtime = "win-x64",
    [switch]$SingleFile,
    [switch]$SkipInstaller,
    [switch]$CreateGitHubRelease,
    [string]$Repository = "huang1988pioneer/Hexoer"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root "src\Hexoer\Hexoer.csproj"
if (-not (Test-Path $Project)) {
    throw "Hexoer project not found at $Project"
}

$PublishDir = Join-Path $Root "artifacts\publish\$Runtime"
$SingleDir = Join-Path $Root "artifacts\single"
$ReleaseDir = Join-Path $Root "artifacts\releases"
$Tag = "v$Version"

function Add-ExistingAsset {
    param(
        [System.Collections.Generic.List[string]]$Assets,
        [string]$Path
    )

    if ($Path -and (Test-Path $Path)) {
        $Assets.Add((Resolve-Path $Path).Path)
    }
}
function Remove-PathWithRetry {
    param([string]$Path)

    if (-not (Test-Path $Path)) { return }

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Remove-Item $Path -Recurse -Force
            return
        } catch {
            if ($attempt -eq 5) { throw }
            Start-Sleep -Milliseconds (300 * $attempt)
        }
    }
}

Write-Host "==> Hexoer publish $Tag ($Runtime)" -ForegroundColor Cyan
Write-Host "    Root: $Root"

$PortableDir = Join-Path $Root "artifacts\portable"
$InstallerDir = Join-Path $Root "artifacts\installer"
foreach ($d in @($SingleDir, $ReleaseDir, $PortableDir, $InstallerDir)) {
    Remove-PathWithRetry $d
    New-Item -ItemType Directory -Path $d -Force | Out-Null
}

$publishScript = Join-Path $PSScriptRoot "publish-windows.ps1"
$publishArgs = @{
    Configuration = "Release"
    Runtime = $Runtime
    Version = $Version
}
if ($SingleFile) {
    & $publishScript @publishArgs -SingleFile
} else {
    & $publishScript @publishArgs
}

$exe = Join-Path $PublishDir "Hexoer.exe"
if (-not (Test-Path $exe)) { throw "Hexoer.exe not found at $exe" }

if ($SingleFile) {
    $singleExe = Join-Path $SingleDir "Hexoer-$Version-$Runtime.exe"
    Copy-Item $exe $singleExe -Force
    $sizeMb = [math]::Round((Get-Item $singleExe).Length / 1MB, 1)
    Write-Host "    Single EXE: $singleExe ($sizeMb MB)" -ForegroundColor Green
}

if (-not $SkipInstaller) {
    $packageScript = Join-Path $PSScriptRoot "build-installer.ps1"
    if ($SingleFile) {
        & $packageScript -Version $Version -SingleFile -SkipPublish
    } else {
        & $packageScript -Version $Version -SkipPublish
    }

    foreach ($path in @("artifacts\portable", "artifacts\installer")) {
        $source = Join-Path $Root $path
        if (Test-Path $source) {
            Copy-Item (Join-Path $source "*") $ReleaseDir -Recurse -Force
        }
    }
} else {
    Write-Host "==> SkipInstaller set; packaging skipped." -ForegroundColor Yellow
}

if ($SingleFile) {
    Copy-Item (Join-Path $SingleDir "Hexoer-$Version-$Runtime.exe") $ReleaseDir -Force
}

$releaseAssets = [System.Collections.Generic.List[string]]::new()
Get-ChildItem $ReleaseDir -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne "Hexoer-$Version-SHA256SUMS.txt" } |
    Sort-Object Name |
    ForEach-Object { $releaseAssets.Add($_.FullName) }

if ($releaseAssets.Count -eq 0) {
    throw "No release files were produced in $ReleaseDir"
}

$checksumPath = Join-Path $ReleaseDir "Hexoer-$Version-SHA256SUMS.txt"
Remove-PathWithRetry $checksumPath
foreach ($asset in $releaseAssets) {
    $hash = Get-FileHash $asset -Algorithm SHA256
    "{0}  {1}" -f $hash.Hash.ToLowerInvariant(), (Split-Path $asset -Leaf) |
        Add-Content -Path $checksumPath -Encoding ASCII
}
Add-ExistingAsset $releaseAssets $checksumPath

Write-Host ""
Write-Host "Release files:" -ForegroundColor Cyan
foreach ($asset in $releaseAssets) {
    $item = Get-Item $asset
    Write-Host ("  - {0} ({1:N1} MB)" -f $item.FullName, ($item.Length / 1MB))
}

if ($CreateGitHubRelease) {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) { throw "GitHub CLI (gh) was not found." }

    Write-Host "==> Publishing GitHub release $Tag to $Repository" -ForegroundColor Cyan
    & $gh.Source release view $Tag --repo $Repository *> $null
    if ($LASTEXITCODE -eq 0) {
        & $gh.Source release upload $Tag @releaseAssets --repo $Repository --clobber
    } else {
        & $gh.Source release create $Tag @releaseAssets --repo $Repository --title "Hexoer $Tag" --notes "Hexoer $Tag Windows release."
    }

    if ($LASTEXITCODE -ne 0) { throw "gh release failed ($LASTEXITCODE)" }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Publish folder : $PublishDir"
Write-Host "  Single EXE     : $SingleDir"
Write-Host "  Release files  : $ReleaseDir"
