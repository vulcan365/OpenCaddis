#!/usr/bin/env pwsh
param([switch]$DryRun)

$lastTag = git tag --list 'v*.*.*' | Sort-Object { [System.Version]($_ -replace '^v', '') } | Select-Object -Last 1
$version = $lastTag -replace '^v', ''
$parts = $version -split '\.'

$newTag = "v$([int]$parts[0]).$([int]$parts[1] + 1).0"

Write-Host "Current: $lastTag"
Write-Host "New:     $newTag"

if ($DryRun) { Write-Host "[DryRun] Would create and push $newTag" -ForegroundColor Yellow; exit 0 }

$confirm = Read-Host "Tag and push $newTag? (y/n)"
if ($confirm -ne 'y') { Write-Host "Aborted." -ForegroundColor Red; exit 0 }

$currentBranch = git rev-parse --abbrev-ref HEAD
if ($currentBranch -ne 'main') { Write-Error "Must be on main. Currently on: $currentBranch"; exit 1 }

git pull origin main
git tag $newTag
git push origin $newTag

Write-Host "Released $newTag" -ForegroundColor Green