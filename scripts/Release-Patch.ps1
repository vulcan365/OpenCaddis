#!/usr/bin/env pwsh

param(
    [switch]$DryRun
)

# Get the latest semver tag
$lastTag = git tag --list 'v*.*.*' | Sort-Object { [System.Version]($_ -replace '^v', '') } | Select-Object -Last 1

if (-not $lastTag) {
    Write-Error "No existing version tags found. Create an initial tag first (e.g. git tag v0.1.0)"
    exit 1
}

# Parse the version
$version = $lastTag -replace '^v', ''
$parts = $version -split '\.'

$major = [int]$parts[0]
$minor = [int]$parts[1]
$patch = [int]$parts[2]

# Increment patch
$patch++

$newTag = "v$major.$minor.$patch"

Write-Host "Current: $lastTag"
Write-Host "New:     $newTag"

if ($DryRun) {
    Write-Host "[DryRun] Would create and push tag $newTag" -ForegroundColor Yellow
    exit 0
}

# Confirm
$confirm = Read-Host "Tag and push $newTag? (y/n)"
if ($confirm -ne 'y') {
    Write-Host "Aborted." -ForegroundColor Red
    exit 0
}

# Make sure we're on main and up to date
$currentBranch = git rev-parse --abbrev-ref HEAD
if ($currentBranch -ne 'main') {
    Write-Error "You must be on main to release. Currently on: $currentBranch"
    exit 1
}

git pull origin main

# Create and push the tag
git tag $newTag
git push origin $newTag

Write-Host "Released $newTag — GitHub Actions will build and push the Docker image." -ForegroundColor Green