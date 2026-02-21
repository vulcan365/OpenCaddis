#!/usr/bin/env pwsh

param(
    [switch]$DryRun
)

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Write-Error 'git not found'; exit 1 }

$lastTag = git tag --list 'v*.*.*' | Sort-Object { [System.Version]($_ -replace '^v', '') } | Select-Object -Last 1

if (-not $lastTag) {
    Write-Error 'No existing version tags found. Create an initial tag first (e.g. git tag v0.1.0)'
    exit 1
}

$parts  = ($lastTag -replace '^v', '') -split '\.'
$newTag = "v$([int]$parts[0] + 1).0.0"

Write-Host "Current: $lastTag"
Write-Host "New:     $newTag"

if ($DryRun) {
    Write-Host "[DryRun] Would create and push tag $newTag." -ForegroundColor Yellow
    exit 0
}

$currentBranch = git rev-parse --abbrev-ref HEAD
if ($currentBranch -ne 'main') {
    Write-Error "You must be on main to release. Currently on: $currentBranch"
    exit 1
}

Write-Host 'WARNING: Major version bump. This signals a breaking change.' -ForegroundColor Yellow
$confirm = Read-Host 'Tag and release? (y/n)'
if ($confirm -ne 'y') { Write-Host 'Aborted.' -ForegroundColor Red; exit 0 }

git pull origin main
git tag $newTag
git push origin $newTag

Write-Host "Tag $newTag pushed - GitHub Actions is building and pushing the Docker image." -ForegroundColor Green
Write-Host 'Create the release notes at:' -ForegroundColor Cyan
Write-Host "  https://github.com/vulcan365/OpenCaddis/releases/new?tag=$newTag" -ForegroundColor Cyan