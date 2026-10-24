# Build & Deploy Script for Jellyfin Custom Hover Trailer Suite
# Usage: .\deploy-plugin.ps1

$ErrorActionPreference = "Stop"

Write-Host "1. Building JellyfinTrailerSuite (.NET 9.0)..." -ForegroundColor Cyan
$dotnetExe = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnetExe)) {
    $dotnetExe = "dotnet"
}
& $dotnetExe build .\JellyfinTrailerSuite.sln -c Release
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

$pluginOutputDir = ".\src\Jellyfin.Plugin.CustomHoverTrailer\bin\Release\net9.0"
$pluginDll = Join-Path $pluginOutputDir "Jellyfin.Plugin.CustomHoverTrailer.dll"

# Locate Jellyfin's plugins directory on Windows
$candidateDataDirs = @(
    "C:\ProgramData\Jellyfin\ServerData\plugins\CustomHoverTrailer",
    "C:\ProgramData\Jellyfin\Server\plugins\CustomHoverTrailer",
    (Join-Path $env:LOCALAPPDATA "jellyfin\plugins\CustomHoverTrailer"),
    (Join-Path $env:APPDATA "jellyfin\plugins\CustomHoverTrailer")
)

$targetDir = $candidateDataDirs[0]
foreach ($dir in $candidateDataDirs) {
    $parent = Split-Path $dir -Parent
    if (Test-Path $parent) {
        $targetDir = $dir
        break
    }
}

Write-Host "2. Installing plugin and dependencies to: $targetDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

# Copy the plugin DLL and its runtime dependencies (e.g. YoutubeExplode.dll, AngleSharp.dll)
# while excluding Jellyfin/Microsoft host assemblies that Jellyfin already provides
$excludePrefixes = @("Jellyfin.", "MediaBrowser.", "Emby.", "Microsoft.", "System.", "netstandard")
$dllFiles = Get-ChildItem -Path $pluginOutputDir -Filter "*.dll" | Where-Object {
    $name = $_.Name
    $shouldExclude = $false
    foreach ($prefix in $excludePrefixes) {
        if ($name -like "$prefix*" -and $name -ne "Jellyfin.Plugin.CustomHoverTrailer.dll") {
            $shouldExclude = $true
            break
        }
    }
    -not $shouldExclude
}

foreach ($dll in $dllFiles) {
    $destPath = Join-Path $targetDir $dll.Name
    try {
        Copy-Item -Path $dll.FullName -Destination $destPath -Force
        Write-Host "   Copied: $($dll.Name)" -ForegroundColor Gray
    }
    catch {
        # If Jellyfin Server is currently running and holding a lock on the DLL,
        # Windows allows renaming an in-use DLL and then placing the new DLL at the original path!
        $oldLockedPath = "$destPath.old_$(Get-Date -Format 'HHmmss')"
        if (Test-Path $destPath) {
            Rename-Item -Path $destPath -NewName (Split-Path $oldLockedPath -Leaf) -Force
        }
        Copy-Item -Path $dll.FullName -Destination $destPath -Force
        Write-Host "   Replaced locked DLL: $($dll.Name) (restart Jellyfin to load new version)" -ForegroundColor Yellow
    }
}

# Create meta.json so Jellyfin 10.11.3 loads the local plugin cleanly
$metaJson = @{
    category = "General"
    changelog = "v1.1: YoutubeExplode integration, dedicated DLL file logger, browser console diagnostics, 4-mode trailer generator"
    description = "Plays 30-second YouTube-style hover previews directly inside movie/series cards from same-folder *-trailer.mp4 files, remote .mp4 URLs, and YouTube links."
    guid = "9b7e4c12-6d38-4f2a-8c1e-5a9d7b3e1f40"
    name = "Custom Hover Trailer"
    overview = "In-card hover previews for local *-trailer.mp4, remote MP4s, and YouTube"
    owner = "Custom"
    targetAbi = "10.11.3.0"
    timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    version = "1.1.0.0"
    status = "Active"
    autoUpdate = $false
    assemblies = @($dllFiles | ForEach-Object { $_.Name })
} | ConvertTo-Json -Depth 4

Set-Content -Path (Join-Path $targetDir "meta.json") -Value $metaJson -Encoding UTF8

Write-Host "Done! Restart your Jellyfin Server service for the plugin to take effect." -ForegroundColor Green
Write-Host "WinForms Trailer Generator executable is at: .\src\TrailerGenerator.WinForms\bin\Release\net9.0-windows\TrailerGenerator.exe" -ForegroundColor Yellow
