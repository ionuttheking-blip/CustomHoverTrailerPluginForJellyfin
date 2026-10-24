<#
.SYNOPSIS
    Compiles the Jellyfin plugin locally, packages the release ZIP into /releases,
    calculates the MD5 checksum, and automatically updates manifest.json.

.DESCRIPTION
    Builds the solution locally without consuming any GitHub Actions minutes.
    The resulting release zip is saved into the 'releases/' folder and tracked in Git,
    making it instantly downloadable by Jellyfin servers via raw.githubusercontent.com.

.EXAMPLE
    .\scripts\package-release.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$ScriptRoot = Split-Path -Parent $PSScriptRoot
if (-not $ScriptRoot) { $ScriptRoot = (Get-Location).Path }

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   Jellyfin Custom Hover Trailer - Local Release Packager " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Read Version from .csproj
$csprojPath = Join-Path $ScriptRoot "src\Jellyfin.Plugin.CustomHoverTrailer\Jellyfin.Plugin.CustomHoverTrailer.csproj"
if (-not (Test-Path $csprojPath)) {
    throw "Could not locate project file at $csprojPath"
}

[xml]$csprojXml = Get-Content $csprojPath
$version = $csprojXml.Project.PropertyGroup.Version
if (-not $version) { $version = "1.1.0.0" }
$version = $version.Trim()

Write-Host "`n[1/5] Target Version: $version" -ForegroundColor Green

# 2. Build in Release Mode
Write-Host "`n[2/5] Compiling plugin in Release mode..." -ForegroundColor Yellow
$dotnet = "dotnet"
if ($env:LOCALAPPDATA -and (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe")) {
    $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
}

& $dotnet build $csprojPath -c Release --warnaserror
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

$buildOutputDir = Join-Path $ScriptRoot "src\Jellyfin.Plugin.CustomHoverTrailer\bin\Release\net9.0"
$pluginDll = Join-Path $buildOutputDir "Jellyfin.Plugin.CustomHoverTrailer.dll"
$youtubeExplodeDll = Join-Path $buildOutputDir "YoutubeExplode.dll"

if (-not (Test-Path $pluginDll)) {
    throw "Compiled plugin assembly not found at: $pluginDll"
}

# 3. Create Release Zip Archive
Write-Host "`n[3/5] Packaging release zip archive..." -ForegroundColor Yellow
$releasesDir = Join-Path $ScriptRoot "releases"
if (-not (Test-Path $releasesDir)) {
    New-Item -ItemType Directory -Path $releasesDir -Force | Out-Null
}

$zipName = "CustomHoverTrailer_${version}.zip"
$zipPath = Join-Path $releasesDir $zipName

if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
}

$filesToZip = @($pluginDll)
if (Test-Path $youtubeExplodeDll) {
    $filesToZip += $youtubeExplodeDll
}

Compress-Archive -Path $filesToZip -DestinationPath $zipPath -Force
Write-Host "Created: $zipPath" -ForegroundColor Green

# 4. Calculate Checksums
Write-Host "`n[4/5] Computing MD5 and SHA256 checksums..." -ForegroundColor Yellow
$md5 = (Get-FileHash -Path $zipPath -Algorithm MD5).Hash.ToLowerInvariant()
$sha256 = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host "MD5 Checksum   : $md5" -ForegroundColor Cyan
Write-Host "SHA256 Checksum: $sha256" -ForegroundColor Gray

# 5. Update manifest.json
Write-Host "`n[5/5] Updating manifest.json..." -ForegroundColor Yellow
$manifestPath = Join-Path $ScriptRoot "manifest.json"
$timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

$sourceUrl = "https://raw.githubusercontent.com/ionuttheking-blip/CustomHoverTrailerPluginForJellyfin/main/releases/$zipName"

$manifestContent = @"
[
  {
    "guid": "9b7e4c12-6d38-4f2a-8c1e-5a9d7b3e1f40",
    "name": "Custom Hover Trailer",
    "description": "Plays YouTube-style hover preview trailers directly inside movie and series cards from local *-trailer.mp4 files, remote URLs, and YouTube links.",
    "overview": "Adds Netflix/YouTube-style hover trailers to Jellyfin Web client cards.",
    "owner": "ionuttheking-blip",
    "category": "General",
    "versions": [
      {
        "version": "$version",
        "changelog": "Release ${version}: Local *-trailer.mp4 playback, YouTube stream fallback, web client card injection, and hover customization settings.",
        "targetAbi": "10.11.0.0",
        "sourceUrl": "$sourceUrl",
        "checksum": "$md5",
        "timestamp": "$timestamp"
      }
    ]
  }
]
"@

Set-Content -Path $manifestPath -Value $manifestContent -Encoding UTF8
Write-Host "Updated manifest.json with version $version and checksum $md5" -ForegroundColor Green

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host " Package Complete! Zero GitHub Actions minutes consumed." -ForegroundColor Green
Write-Host " Run the following to publish to GitHub:" -ForegroundColor Yellow
Write-Host "   git add releases manifest.json" -ForegroundColor White
Write-Host "   git commit -m `"release: v$version`"" -ForegroundColor White
Write-Host "   git push" -ForegroundColor White
Write-Host "==========================================================" -ForegroundColor Green
