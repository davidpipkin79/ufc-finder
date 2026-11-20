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

Write-Host "Building MSI..." -ForegroundColor Cyan

dotnet build (Join-Path $installerDir "Package.wixproj") -c $Configuration

if ($LASTEXITCODE -ne 0) {
    throw "MSI build failed."
}

# Payload for the setup chain: the WebView2 Evergreen bootstrapper (about 1.8 MB). It is
# downloaded once and reused; the runtime itself is fetched by that bootstrapper on the
# target machine, and skipped entirely when the runtime is already installed.
$webView2 = Join-Path $installerDir "MicrosoftEdgeWebView2Setup.exe"

if (-not (Test-Path $webView2)) {
    Write-Host "Downloading the WebView2 runtime bootstrapper..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $webView2 -UseBasicParsing
}

Write-Host "Building setup bundle..." -ForegroundColor Cyan

dotnet build (Join-Path $installerDir "Bundle.wixproj") -c $Configuration

if ($LASTEXITCODE -ne 0) {
    throw "Bundle build failed."
}

$outputDir = Join-Path $installerDir "bin\$Configuration"

$msi = Get-ChildItem $outputDir -Filter *.msi | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$setup = Get-ChildItem $outputDir -Filter *.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $msi) { throw "No MSI was produced." }
if (-not $setup) { throw "No setup executable was produced." }

Write-Host ""
Write-Host ("Setup (distribute this): " + $setup.FullName + " (" + [math]::Round($setup.Length / 1MB, 1) + " MB)") -ForegroundColor Green
Write-Host ("MSI (used by IT / silent deployment): " + $msi.FullName + " (" + [math]::Round($msi.Length / 1MB, 1) + " MB)") -ForegroundColor Gray
