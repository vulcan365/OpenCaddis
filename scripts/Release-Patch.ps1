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

$version = $lastTag -replace '^v', ''
$parts   = $version -split '\.'
$newTag  = "v$([int]$parts[0]).$([int]$parts[1]).$([int]$parts[2] + 1)"

Write-Host "Current: $lastTag"
Write-Host "New:     $newTag"

if ($DryRun) {
    Write-Host "[DryRun] Would create and push tag $newTag, then switch to develop." -ForegroundColor Yellow
    exit 0
}

$currentBranch = git rev-parse --abbrev-ref HEAD
if ($currentBranch -ne 'main') {
    Write-Error "You must be on main to release. Currently on: $currentBranch"
    exit 1
}

$confirm = Read-Host 'Tag and release? (y/n)'
if ($confirm -ne 'y') { Write-Host 'Aborted.' -ForegroundColor Red; exit 0 }

git pull origin main
git tag $newTag
git push origin $newTag
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to push tag $newTag. Remaining on main."
    exit 1
}

git switch develop
if ($LASTEXITCODE -ne 0) {
    Write-Error "Tag $newTag was pushed, but switching to develop failed."
    exit 1
}

Write-Host "Tag $newTag pushed." -ForegroundColor Green
Write-Host 'Switched to develop.' -ForegroundColor Green
Write-Host 'The Release Windows workflow will build and publish the GitHub release assets.' -ForegroundColor Cyan
