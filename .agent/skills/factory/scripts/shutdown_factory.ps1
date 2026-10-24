<#
.SYNOPSIS
    Factory Shutdown Helper: Prunes temporary task branches and prepares repository for clean state.

.PARAMETER FeatureSlug
    The feature branch name (e.g. feature/my-feature).
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory=$false)]
    [string]$FeatureSlug
)

Write-Host "================================================="
Write-Host "🏭 Shutting Down Factory Session..."
Write-Host "================================================="

# 1. Prune temporary task branches if FeatureSlug was specified
if ($FeatureSlug) {
    Write-Host "Cleaning up temporary task branches under $FeatureSlug/..."
    $taskBranches = git branch --list "$FeatureSlug/*" | ForEach-Object { $_.Trim().TrimStart('*').Trim() }
    foreach ($b in $taskBranches) {
        if ($b -and $b -ne $FeatureSlug) {
            Write-Host "  -> Removing $b"
            git branch -D $b 2>$null
        }
    }
}

# 2. Check current git status
Write-Host "Repository status on current branch:"
git status --short

Write-Host "================================================="
Write-Host "✅ Factory branches cleaned. Persistent subagents can now be terminated."
Write-Host "================================================="
