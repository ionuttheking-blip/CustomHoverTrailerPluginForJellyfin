<#
.SYNOPSIS
    Compiles and deploys the Custom Hover Trailer plugin directly to your local Jellyfin Media Server.

.DESCRIPTION
    Builds the Jellyfin.Plugin.CustomHoverTrailer project in Release mode and copies the required
    assemblies (Jellyfin.Plugin.CustomHoverTrailer.dll and YoutubeExplode.dll) into Jellyfin's plugins folder.

.PARAMETER PluginsDir
    Optional custom path to the Jellyfin plugins folder. If omitted, the script searches standard locations:
    - C:\ProgramData\Jellyfin\Server\plugins
    - $env:LOCALAPPDATA\jellyfin\plugins
    - $env:APPDATA\jellyfin\plugins

.PARAMETER RestartJellyfin
    If specified, automatically stops and restarts the Jellyfin service or process after copying.

.EXAMPLE
    .\scripts\deploy-to-jellyfin.ps1
    .\scripts\deploy-to-jellyfin.ps1 -RestartJellyfin
    .\scripts\deploy-to-jellyfin.ps1 -PluginsDir "D:\Jellyfin\Server\plugins" -RestartJellyfin
#>

[CmdletBinding()]
param(
    [string]$PluginsDir = "",
    [switch]$RestartJellyfin
)

$ErrorActionPreference = "Stop"
$ScriptRoot = Split-Path -Parent $PSScriptRoot
if (-not $ScriptRoot) { $ScriptRoot = (Get-Location).Path }

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   Jellyfin Custom Hover Trailer - Local Deploy Tool      " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Locate Plugins Directory
if (-not $PluginsDir) {
    $potentialPaths = @(
        "C:\ProgramData\Jellyfin\Server\plugins",
        "$env:LOCALAPPDATA\jellyfin\plugins",
        "$env:APPDATA\jellyfin\plugins",
        "$env:JELLYFIN_DATA_DIR\plugins"
    )

    foreach ($path in $potentialPaths) {
        if ($path -and (Test-Path $path)) {
            $PluginsDir = $path
            break
        }
    }
}

if (-not $PluginsDir) {
    # Default to standard server location
    $PluginsDir = "C:\ProgramData\Jellyfin\Server\plugins"
}

Write-Host "[1/4] Target plugins directory: $PluginsDir" -ForegroundColor Green

# 2. Build the Plugin Project
Write-Host "`n[2/4] Compiling Jellyfin.Plugin.CustomHoverTrailer in Release mode..." -ForegroundColor Yellow
$projectPath = Join-Path $ScriptRoot "src\Jellyfin.Plugin.CustomHoverTrailer\Jellyfin.Plugin.CustomHoverTrailer.csproj"
if (-not (Test-Path $projectPath)) {
    throw "Could not find project file at: $projectPath"
}

# Locate dotnet binary
$dotnet = "dotnet"
if ($env:LOCALAPPDATA -and (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe")) {
    $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
}

& $dotnet build $projectPath -c Release --warnaserror
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

$buildOutputDir = Join-Path $ScriptRoot "src\Jellyfin.Plugin.CustomHoverTrailer\bin\Release\net9.0"
$pluginDll = Join-Path $buildOutputDir "Jellyfin.Plugin.CustomHoverTrailer.dll"
$youtubeExplodeDll = Join-Path $buildOutputDir "YoutubeExplode.dll"

if (-not (Test-Path $pluginDll)) {
    throw "Compiled plugin assembly not found at: $pluginDll"
}

# 3. Deploy Assemblies to Plugins Folder
Write-Host "`n[3/4] Deploying plugin files..." -ForegroundColor Yellow
$targetPluginFolder = Join-Path $PluginsDir "CustomHoverTrailer"
if (-not (Test-Path $targetPluginFolder)) {
    New-Item -ItemType Directory -Path $targetPluginFolder -Force | Out-Null
    Write-Host "Created plugin directory: $targetPluginFolder" -ForegroundColor Gray
}

Copy-Item -Path $pluginDll -Destination $targetPluginFolder -Force
Write-Host "Copied: Jellyfin.Plugin.CustomHoverTrailer.dll" -ForegroundColor Gray

if (Test-Path $youtubeExplodeDll) {
    Copy-Item -Path $youtubeExplodeDll -Destination $targetPluginFolder -Force
    Write-Host "Copied: YoutubeExplode.dll" -ForegroundColor Gray
}

Write-Host "Files deployed successfully to $targetPluginFolder" -ForegroundColor Green

# 4. Handle Service/Process Restart
Write-Host "`n[4/4] Checking Jellyfin process / service status..." -ForegroundColor Yellow

$jellyfinService = Get-Service -Name "jellyfin" -ErrorAction SilentlyContinue
$jellyfinProcesses = Get-Process -Name "jellyfin" -ErrorAction SilentlyContinue

if ($RestartJellyfin) {
    if ($jellyfinService -and $jellyfinService.Status -eq "Running") {
        Write-Host "Restarting Jellyfin Windows Service..." -ForegroundColor Cyan
        Restart-Service -Name "jellyfin" -Force
        Write-Host "Jellyfin Windows Service restarted successfully!" -ForegroundColor Green
    }
    elseif ($jellyfinProcesses) {
        Write-Host "Stopping running Jellyfin processes..." -ForegroundColor Cyan
        Stop-Process -Name "jellyfin" -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        Write-Host "Please start your Jellyfin application to load the updated plugin." -ForegroundColor Yellow
    }
    else {
        Write-Host "Jellyfin is not currently running. It will load the plugin on next launch." -ForegroundColor Gray
    }
} else {
    Write-Host "NOTE: To activate the updated plugin, please restart your Jellyfin Media Server." -ForegroundColor Yellow
    Write-Host "Tip: Run with -RestartJellyfin to automate service restart." -ForegroundColor Gray
}

Write-Host "`n Deployment Complete! Plugin is ready in Jellyfin." -ForegroundColor Green
