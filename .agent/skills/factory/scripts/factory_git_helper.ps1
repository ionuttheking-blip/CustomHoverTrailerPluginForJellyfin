<#
.SYNOPSIS
    Factory Git Helper: Manages feature branching, isolated task branching, diff generation, and sequential rebase-merging.

.PARAMETER Action
    CreateFeatureBranch, CreateTaskBranch, GetTaskDiff, RebaseAndMerge, GetCumulativeDiff, PruneTaskBranches, EnsureRepo

.PARAMETER FeatureSlug
    The feature branch name (e.g. feature/my-feature).

.PARAMETER TaskBranch
    The task branch name (e.g. feature/my-feature/task-01).

.PARAMETER BaseBranch
    The base branch to branch from (default: current HEAD or main/master).
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("CreateFeatureBranch", "CreateTaskBranch", "GetTaskDiff", "RebaseAndMerge", "GetCumulativeDiff", "PruneTaskBranches", "EnsureRepo")]
    [string]$Action,

    [Parameter(Mandatory=$false)]
    [string]$FeatureSlug,

    [Parameter(Mandatory=$false)]
    [string]$TaskBranch,

    [Parameter(Mandatory=$false)]
    [string]$BaseBranch = "main"
)

$ErrorActionPreference = "Stop"

function Find-GitBinary {
    $cmd = Get-Command git -ErrorAction SilentlyContinue
    if ($cmd) {
        return "git"
    }
    
    $commonLocations = @(
        "C:\Program Files\Git\cmd\git.exe",
        "C:\Program Files\Git\bin\git.exe",
        "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe"
    )
    foreach ($loc in $commonLocations) {
        if (Test-Path $loc) {
            return $loc
        }
    }
    return $null
}

$gitBin = Find-GitBinary

if (-not $gitBin) {
    Write-Warning "[Factory Git Helper] 'git' executable was not found in PATH or standard installation locations."
    Write-Warning "To install Git, run: winget install --id Git.Git -e --source winget"
    throw "Git executable not found. Please install Git or add it to PATH."
}

function Ensure-GitRepo {
    $isRepo = & $gitBin rev-parse --is-inside-work-tree 2>$null
    if ($LASTEXITCODE -ne 0 -or $isRepo -ne "true") {
        Write-Host "[Factory Git Helper] Initializing new Git repository on current directory with initial commit..."
        & $gitBin init -b main
        & $gitBin add -A
        & $gitBin commit -m "chore: initial commit before factory workflow"
    }
}

Ensure-GitRepo

switch ($Action) {
    "EnsureRepo" {
        Write-Host "Repository verified and ready."
    }

    "CreateFeatureBranch" {
        if (-not $FeatureSlug) { Write-Error "FeatureSlug is required." }
        Write-Host "Creating feature branch '$FeatureSlug' from '$BaseBranch'..."
        
        $hasBase = & $gitBin show-ref --verify --quiet "refs/heads/$BaseBranch"
        if ($LASTEXITCODE -ne 0) {
            $BaseBranch = (& $gitBin branch --show-current)
            if (-not $BaseBranch) { $BaseBranch = "HEAD" }
        }

        & $gitBin checkout -b $FeatureSlug $BaseBranch
        Write-Host "Feature branch '$FeatureSlug' checked out successfully."
    }

    "CreateTaskBranch" {
        if (-not $FeatureSlug -or -not $TaskBranch) { Write-Error "FeatureSlug and TaskBranch are required." }
        Write-Host "Creating task branch '$TaskBranch' off '$FeatureSlug'..."
        
        & $gitBin checkout $FeatureSlug
        & $gitBin checkout -b $TaskBranch $FeatureSlug
        Write-Host "Task branch '$TaskBranch' created and checked out."
    }

    "GetTaskDiff" {
        if (-not $FeatureSlug -or -not $TaskBranch) { Write-Error "FeatureSlug and TaskBranch are required." }
        Write-Host "Generating diff between '$FeatureSlug' and '$TaskBranch'..."
        & $gitBin diff "$FeatureSlug...$TaskBranch"
    }

    "RebaseAndMerge" {
        if (-not $FeatureSlug -or -not $TaskBranch) { Write-Error "FeatureSlug and TaskBranch are required." }
        Write-Host "Rebasing task branch '$TaskBranch' onto latest '$FeatureSlug'..."
        
        & $gitBin checkout $TaskBranch
        $rebaseOut = & $gitBin rebase $FeatureSlug 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Rebase conflict detected on $TaskBranch!"
            & $gitBin rebase --abort 2>$null
            Write-Error "Automatic rebase failed. Implementer must manually resolve conflicts on $TaskBranch."
        }

        Write-Host "Checking out feature branch '$FeatureSlug' and merging '$TaskBranch'..."
        & $gitBin checkout $FeatureSlug
        & $gitBin merge --ff-only $TaskBranch
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Fast-forward merge not possible; performing standard merge..."
            & $gitBin merge --no-ff -m "merge($TaskBranch): integrate approved task into $FeatureSlug" $TaskBranch
        }

        Write-Host "Task branch '$TaskBranch' successfully integrated into '$FeatureSlug'."
    }

    "GetCumulativeDiff" {
        if (-not $FeatureSlug) { Write-Error "FeatureSlug is required." }
        Write-Host "Generating cumulative full-branch diff against '$BaseBranch'..."
        & $gitBin diff "$BaseBranch...$FeatureSlug"
    }

    "PruneTaskBranches" {
        if (-not $FeatureSlug) { Write-Error "FeatureSlug is required." }
        Write-Host "Pruning completed task branches for '$FeatureSlug'..."
        $branches = & $gitBin branch --list "$FeatureSlug/*" | ForEach-Object { $_.Trim().TrimStart('*').Trim() }
        foreach ($b in $branches) {
            if ($b -and $b -ne $FeatureSlug) {
                Write-Host "Deleting task branch $b..."
                & $gitBin branch -D $b 2>$null
            }
        }
        Write-Host "Pruning complete."
    }
}
