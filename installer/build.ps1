<#
    Builds the UFC Event Host Finder installer.

    Publishes the app as a self-contained win-x64 build (so the target machine needs no
    .NET runtime of its own), then packages that output into an MSI with WiX.

    Usage:  pwsh -File installer\build.ps1
#>

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$installerDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $installerDir
$project = Join-Path $repoRoot "UFCFinderApp\UFCFinderApp.csproj"
$publishDir = Join-Path $installerDir "publish"

Write-Host "Publishing $project ($Runtime, self-contained)..." -ForegroundColor Cyan

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $publishDir `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "Publish failed."
}

Write-Host "Building installer..." -ForegroundColor Cyan

dotnet build (Join-Path $installerDir "Package.wixproj") -c $Configuration

if ($LASTEXITCODE -ne 0) {
    throw "Installer build failed."
}

$msi = Get-ChildItem (Join-Path $installerDir "bin\$Configuration") -Recurse -Filter *.msi |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $msi) {
    throw "No MSI was produced."
}

$sizeMb = [math]::Round($msi.Length / 1MB, 1)

Write-Host ""
Write-Host "Installer ready: $($msi.FullName) ($sizeMb MB)" -ForegroundColor Green
