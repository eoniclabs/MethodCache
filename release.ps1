#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Creates a new release of MethodCache
.DESCRIPTION
    This script automates the release process by:
    1. Validating the version number
    2. Updating version.json using nbgv
    3. Committing the version change
    4. Creating and pushing a git tag
    5. Triggering the GitHub Actions publish workflow

    When run without parameters, shows suggested version numbers based on the current version.
.PARAMETER Version
    The semantic version number (e.g., 1.0.0, 1.2.3-beta). Optional - if not provided, shows suggestions.
.PARAMETER SkipTests
    Skip running tests before release (not recommended)
.EXAMPLE
    .\release.ps1
    Shows suggested version numbers based on current version
.EXAMPLE
    .\release.ps1 -Version 1.0.0
.EXAMPLE
    .\release.ps1 -Version 1.2.0-beta
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[a-zA-Z0-9\-\.]+)?$')]
    [string]$Version,

    [Parameter(Mandatory=$false)]
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

# Colors for output
function Write-Success { Write-Host $args -ForegroundColor Green }
function Write-Info { Write-Host $args -ForegroundColor Cyan }
function Write-Warning { Write-Host $args -ForegroundColor Yellow }
function Write-Error { Write-Host $args -ForegroundColor Red }

# Function to get current version from version.json
function Get-CurrentVersion {
    $versionJson = Get-Content "version.json" | ConvertFrom-Json
    return $versionJson.version
}

# Function to suggest next version
function Get-SuggestedVersions {
    param([string]$currentVersion)

    # Parse current version
    if ($currentVersion -match '^(\d+)\.(\d+)\.(\d+)(-([a-zA-Z0-9\-\.]+))?$') {
        $major = [int]$matches[1]
        $minor = [int]$matches[2]
        $patch = [int]$matches[3]
        $prerelease = $matches[5]

        # Suggest versions
        $suggestions = @{
            Patch = "$major.$minor.$($patch + 1)"
            Minor = "$major.$($minor + 1).0"
            Major = "$($major + 1).0.0"
        }

        if ($prerelease) {
            # If current is prerelease, suggest stable version
            $suggestions["Stable"] = "$major.$minor.$patch"
        } else {
            # Suggest alpha/beta versions
            $suggestions["Alpha"] = "$major.$($minor + 1).0-alpha"
            $suggestions["Beta"] = "$major.$($minor + 1).0-beta"
        }

        return $suggestions
    }

    return @{}
}

# If version not provided, show suggestions
if (-not $Version) {
    $currentVersion = Get-CurrentVersion
    Write-Info "Current version: $currentVersion"
    Write-Info ""
    Write-Info "Suggested versions:"

    $suggestions = Get-SuggestedVersions -currentVersion $currentVersion
    foreach ($key in $suggestions.Keys | Sort-Object) {
        Write-Info "  $key : $($suggestions[$key])"
    }

    Write-Info ""
    Write-Info "Usage: .\release.ps1 -Version <version>"
    Write-Info "Example: .\release.ps1 -Version $($suggestions['Minor'])"
    exit 0
}

# Ensure we're on main branch
Write-Info "Checking current branch..."
$currentBranch = git rev-parse --abbrev-ref HEAD
if ($currentBranch -ne "main") {
    Write-Error "❌ You must be on the 'main' branch to create a release. Current branch: $currentBranch"
    exit 1
}

# Ensure working directory is clean
Write-Info "Checking working directory status..."
$status = git status --porcelain
if ($status) {
    Write-Error "❌ Working directory has uncommitted changes. Please commit or stash them first."
    git status --short
    exit 1
}

# Pull latest changes
Write-Info "Pulling latest changes..."
git pull
if ($LASTEXITCODE -ne 0) {
    Write-Error "❌ Failed to pull latest changes"
    exit 1
}

# Check if tag already exists
$tagName = "v$Version"
$existingTag = git tag -l $tagName
if ($existingTag) {
    Write-Error "❌ Tag $tagName already exists. Use a different version number."
    exit 1
}

# Run tests unless skipped
if (-not $SkipTests) {
    Write-Info "Running tests..."
    dotnet test --configuration Release
    if ($LASTEXITCODE -ne 0) {
        Write-Error "❌ Tests failed. Fix tests before releasing."
        exit 1
    }
    Write-Success "✓ All tests passed"
}

# Update version using nbgv
Write-Info "Updating version.json to $Version..."
dotnet tool restore
if ($LASTEXITCODE -ne 0) {
    Write-Error "❌ Failed to restore dotnet tools"
    exit 1
}

dotnet nbgv set-version $Version
if ($LASTEXITCODE -ne 0) {
    Write-Error "❌ Failed to set version using nbgv"
    exit 1
}

# Commit version change
Write-Info "Committing version change..."
git add version.json
git commit -m "Bump version to $Version"
if ($LASTEXITCODE -ne 0) {
    Write-Error "❌ Failed to commit version change"
    exit 1
}

# Push commit
Write-Info "Pushing commit to remote..."
git push
if ($LASTEXITCODE -ne 0) {
    Write-Error "❌ Failed to push commit"
    exit 1
}

# Create and push tag
Write-Info "Creating tag $tagName..."
git tag $tagName
if ($LASTEXITCODE -ne 0) {
    Write-Error "❌ Failed to create tag"
    exit 1
}

Write-Info "Pushing tag $tagName..."
git push origin $tagName
if ($LASTEXITCODE -ne 0) {
    Write-Error "❌ Failed to push tag"
    # Cleanup: delete local tag
    git tag -d $tagName
    exit 1
}

Write-Success ""
Write-Success "✓ Release $Version created successfully!"
Write-Success ""
Write-Info "Next steps:"
Write-Info "1. Monitor the workflow: https://github.com/eoniclabs/MethodCache/actions"
Write-Info "2. Verify packages: https://github.com/eoniclabs/MethodCache/packages"
Write-Info "3. Update release notes: https://github.com/eoniclabs/MethodCache/releases/tag/$tagName"
Write-Success ""