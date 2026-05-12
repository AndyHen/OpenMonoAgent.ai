$ErrorActionPreference = "Stop"
$repo = "C:\Users\Andrej\OpenMonoAgent.ai"
$publishDir = "$repo\publish"
$project = "$repo\src\OpenMono.Cli\OpenMono.Cli.csproj"

Write-Host "=== OpenMono Windows Updater ===" -ForegroundColor Cyan

# Check git status
git -C $repo checkout main
if (-not $?) { Write-Host "Failed to checkout main." -ForegroundColor Red; exit 1 }

$original = git -C $repo branch --show-current
Write-Host "Current branch: $original"

# Fetch upstream
Write-Host "`nFetching upstream..." -ForegroundColor Yellow
git -C $repo fetch origin
if (-not $?) { Write-Host "Fetch failed." -ForegroundColor Red; exit 1 }

# Check if we're behind upstream
$behind = git -C $repo rev-list --count "main..origin/main"
if ($behind -gt 0) {
    Write-Host "$behind new commit(s) from upstream."

    # Stash uncommitted changes
    git -C $repo stash
    $hasStash = $?

    # Merge upstream into local main
    Write-Host "Merging upstream..."
    git -C $repo merge origin/main
    if (-not $?) {
        Write-Host "Merge failed." -ForegroundColor Red
        if ($hasStash) { git -C $repo stash pop 2>$null }
        exit 1
    }

    # Check for unresolved conflicts
    $conflicts = git -C $repo diff --name-only --diff-filter=U
    if ($conflicts) {
        Write-Host "`nMerge conflicts in:" -ForegroundColor Red
        Write-Host $conflicts
        if ($hasStash) { git -C $repo stash pop 2>$null }
        Write-Host "`nResolve conflicts, commit, and re-run the script." -ForegroundColor Yellow
        exit 1
    }

    # Restore stashed changes
    if ($hasStash) {
        git -C $repo stash pop
        if (-not $?) { Write-Host "Warning: Failed to restore stashed changes." -ForegroundColor Yellow }
    }
} else {
    Write-Host "Already up to date with upstream." -ForegroundColor Green
}

# Push merged result to fork
Write-Host "`nPushing to fork..." -ForegroundColor Yellow
git -C $repo push fork main --no-verify
if (-not $?) { Write-Host "Failed to push to fork." -ForegroundColor Yellow }

# Build
Write-Host "`nBuilding release..." -ForegroundColor Yellow
dotnet publish $project -c Release -o $publishDir --nologo -v quiet
if (-not $?) { Write-Host "Build failed." -ForegroundColor Red; exit 1 }

# Verify
$version = & "$publishDir\openmono.exe" --version 2>&1
Write-Host "`nInstalled: $version" -ForegroundColor Green
Write-Host "Done." -ForegroundColor Cyan
