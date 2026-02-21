#!/usr/bin/env pwsh

param(
    [switch]$DryRun
)

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Write-Error 'git not found'; exit 1 }

# Make sure we're clean
$uncommitted = git status --porcelain
if ($uncommitted) {
    Write-Error 'You have uncommitted changes. Commit or stash them first.'
    exit 1
}

# Pull latest of both branches
Write-Host 'Fetching latest...' -ForegroundColor Cyan
git fetch origin

# Check develop is up to date
git checkout develop
git pull origin develop

# Show what's coming into main
Write-Host ''
Write-Host 'Commits to be merged into main:' -ForegroundColor Cyan
git log origin/main..develop --oneline
Write-Host ''

if ($DryRun) {
    Write-Host '[DryRun] Would merge develop into main via merge commit.' -ForegroundColor Yellow
    exit 0
}

$confirm = Read-Host 'Merge develop into main? (y/n)'
if ($confirm -ne 'y') { Write-Host 'Aborted.' -ForegroundColor Red; exit 0 }

# Switch to main and merge with explicit no-ff to force a merge commit
git checkout main
git pull origin main
git merge develop --no-ff --no-edit

if ($LASTEXITCODE -ne 0) {
    Write-Error 'Merge conflict - resolve manually, then push main.'
    exit 1
}

git push origin main

Write-Host ''
Write-Host 'develop merged into main.' -ForegroundColor Green
Write-Host 'Run one of the following to tag and release:' -ForegroundColor Cyan
Write-Host '  .\scripts\Release-Patch.ps1'
Write-Host '  .\scripts\Release-Minor.ps1'
Write-Host '  .\scripts\Release-Major.ps1'
Write-Host ''