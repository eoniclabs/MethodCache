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
.PARAMETER Status
    Check the status of a release (whether packages were published)
.PARAMETER Cleanup
    Clean up a failed release (delete packages, tags, revert commit)
.PARAMETER Retry
    Retry a failed release (re-trigger workflow)
.EXAMPLE
    .\release.ps1
    Shows suggested version numbers based on current version
.EXAMPLE
    .\release.ps1 -Version 1.0.0
.EXAMPLE
    .\release.ps1 -Version 1.2.0-beta
.EXAMPLE
    .\release.ps1 -Status 1.2.0
    Check if version 1.2.0 was published successfully
.EXAMPLE
    .\release.ps1 -Retry 1.2.0
    Retry a failed 1.2.0 release
.EXAMPLE
    .\release.ps1 -Cleanup 1.2.0
    Clean up a failed 1.2.0 release
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[a-zA-Z0-9\-\.]+)?$')]
    [string]$Version,

    [Parameter(Mandatory=$false)]
    [switch]$SkipTests,

    [Parameter(Mandatory=$false)]
    [string]$Status,

    [Parameter(Mandatory=$false)]
    [string]$Cleanup,

    [Parameter(Mandatory=$false)]
    [string]$Retry
)

$ErrorActionPreference = "Stop"

# GitHub organization/owner
$GitHubOrg = "eoniclabs"
$GitHubRepo = "MethodCache"

# Package names to check/manage
$Packages = @(
    "MethodCache"
    "MethodCache.Abstractions"
    "MethodCache.Analyzers"
    "MethodCache.Core"
    "MethodCache.OpenTelemetry"
    "MethodCache.Providers.Memory"
    "MethodCache.Providers.Redis"
    "MethodCache.Providers.SqlServer"
    "MethodCache.SourceGenerator"
)

# Colors for output
function Write-Success { Write-Host $args -ForegroundColor Green }
function Write-Info { Write-Host $args -ForegroundColor Cyan }
function Write-Warn { Write-Host $args -ForegroundColor Yellow }
function Write-Err { Write-Host $args -ForegroundColor Red }

# Function to get current version from version.json
function Get-CurrentVersion {
    $versionJson = Get-Content "version.json" | ConvertFrom-Json
    return $versionJson.version
}

# Function to check if a package version exists on GitHub Packages
function Test-PackageVersion {
    param(
        [string]$Package,
        [string]$PackageVersion
    )

    try {
        $versions = gh api "/orgs/$GitHubOrg/packages/nuget/$Package/versions" 2>$null | ConvertFrom-Json
        return ($versions | Where-Object { $_.name -eq $PackageVersion }) -ne $null
    } catch {
        return $false
    }
}

# Function to check release status
function Get-ReleaseStatus {
    param([string]$ReleaseVersion)

    $tagName = "v$ReleaseVersion"

    Write-Info "Checking release status for version $ReleaseVersion..."
    Write-Info ""

    # Check if tag exists locally
    $localTag = git tag -l $tagName
    if ($localTag) {
        Write-Success "✓ Local tag $tagName exists"
    } else {
        Write-Warn "✗ Local tag $tagName does not exist"
    }

    # Check if tag exists on remote
    $remoteTag = git ls-remote --tags origin "refs/tags/$tagName" 2>$null
    if ($remoteTag) {
        Write-Success "✓ Remote tag $tagName exists"
    } else {
        Write-Warn "✗ Remote tag $tagName does not exist"
    }

    # Check GitHub Actions workflow status
    Write-Info ""
    Write-Info "Checking workflow runs for tag $tagName..."
    try {
        $runs = gh run list --workflow=publish.yml --limit=5 --json headBranch,status,conclusion,url 2>$null | ConvertFrom-Json
        $runForTag = $runs | Where-Object { $_.headBranch -eq $tagName } | Select-Object -First 1

        if ($runForTag) {
            if ($runForTag.status -eq "completed") {
                if ($runForTag.conclusion -eq "success") {
                    Write-Success "✓ Workflow completed successfully"
                } else {
                    Write-Err "✗ Workflow failed (conclusion: $($runForTag.conclusion))"
                    Write-Info "  View details: $($runForTag.url)"
                }
            } else {
                Write-Warn "⏳ Workflow still running (status: $($runForTag.status))"
                Write-Info "  View details: $($runForTag.url)"
            }
        } else {
            Write-Warn "✗ No workflow run found for tag $tagName"
        }
    } catch {
        Write-Warn "Could not check workflow status"
    }

    # Check published packages
    Write-Info ""
    Write-Info "Checking published packages..."
    $allPublished = $true
    $anyPublished = $false

    foreach ($package in $Packages) {
        if (Test-PackageVersion -Package $package -PackageVersion $ReleaseVersion) {
            Write-Success "  ✓ $package@$ReleaseVersion published"
            $anyPublished = $true
        } else {
            Write-Warn "  ✗ $package@$ReleaseVersion NOT published"
            $allPublished = $false
        }
    }

    Write-Info ""
    if ($allPublished) {
        Write-Success "✓ All packages published successfully!"
        return 0
    } elseif ($anyPublished) {
        Write-Err "⚠ PARTIAL RELEASE: Some packages were published, some were not."
        Write-Info "  This indicates a failed release that needs cleanup."
        Write-Info "  Run: .\release.ps1 -Cleanup $ReleaseVersion"
        return 2
    } else {
        Write-Warn "✗ No packages published for version $ReleaseVersion"
        Write-Info "  The release may have failed before publishing."
        Write-Info "  Run: .\release.ps1 -Retry $ReleaseVersion"
        return 1
    }
}

# Function to delete a package version from GitHub Packages
function Remove-PackageVersion {
    param(
        [string]$Package,
        [string]$PackageVersion
    )

    try {
        $versions = gh api "/orgs/$GitHubOrg/packages/nuget/$Package/versions" 2>$null | ConvertFrom-Json
        $versionEntry = $versions | Where-Object { $_.name -eq $PackageVersion } | Select-Object -First 1

        if ($versionEntry) {
            Write-Info "  Deleting $Package@$PackageVersion (ID: $($versionEntry.id))..."
            gh api --method DELETE "/orgs/$GitHubOrg/packages/nuget/$Package/versions/$($versionEntry.id)" 2>$null
            Write-Success "  ✓ Deleted $Package@$PackageVersion"
            return $true
        } else {
            Write-Info "  $Package@$PackageVersion not found, skipping"
            return $true
        }
    } catch {
        Write-Err "  ✗ Failed to delete $Package@$PackageVersion"
        return $false
    }
}

# Function to cleanup a failed release
function Invoke-ReleaseCleanup {
    param([string]$ReleaseVersion)

    $tagName = "v$ReleaseVersion"

    Write-Warn "⚠ This will delete all traces of version $ReleaseVersion"
    Write-Warn "  - Delete published packages from GitHub Packages"
    Write-Warn "  - Delete local and remote tags"
    Write-Warn "  - Revert version.json commit (if it's the HEAD)"
    Write-Info ""

    $confirm = Read-Host "Are you sure you want to continue? (y/N)"
    if ($confirm -notmatch '^[Yy]$') {
        Write-Info "Cleanup cancelled."
        return
    }

    Write-Info ""
    Write-Info "Starting cleanup for version $ReleaseVersion..."

    # Delete published packages
    Write-Info ""
    Write-Info "Deleting published packages..."
    foreach ($package in $Packages) {
        Remove-PackageVersion -Package $package -PackageVersion $ReleaseVersion
    }

    # Delete remote tag
    Write-Info ""
    Write-Info "Deleting remote tag..."
    $remoteTag = git ls-remote --tags origin "refs/tags/$tagName" 2>$null
    if ($remoteTag) {
        git push origin --delete $tagName 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Success "✓ Deleted remote tag $tagName"
        } else {
            Write-Err "✗ Failed to delete remote tag $tagName"
        }
    } else {
        Write-Info "Remote tag $tagName does not exist, skipping"
    }

    # Delete local tag
    Write-Info ""
    Write-Info "Deleting local tag..."
    $localTag = git tag -l $tagName
    if ($localTag) {
        git tag -d $tagName
        Write-Success "✓ Deleted local tag $tagName"
    } else {
        Write-Info "Local tag $tagName does not exist, skipping"
    }

    # Check if we should revert the version commit
    Write-Info ""
    Write-Info "Checking version.json commit..."
    $headMessage = git log -1 --format=%s
    if ($headMessage -eq "Bump version to $ReleaseVersion") {
        Write-Warn "HEAD commit is the version bump. Reverting..."
        git revert --no-commit HEAD
        git commit -m "Revert version bump to $ReleaseVersion (release failed)"
        git push
        Write-Success "✓ Reverted version commit"
    } else {
        Write-Info "HEAD commit is not the version bump, skipping revert"
    }

    Write-Info ""
    Write-Success "✓ Cleanup complete!"
    Write-Info ""
    Write-Info "You can now retry the release with:"
    Write-Info "  .\release.ps1 -Version $ReleaseVersion"
}

# Function to retry a failed release (re-trigger workflow)
function Invoke-ReleaseRetry {
    param([string]$ReleaseVersion)

    $tagName = "v$ReleaseVersion"

    Write-Info "Checking if we can retry release $ReleaseVersion..."

    # Check if tag exists on remote
    $remoteTag = git ls-remote --tags origin "refs/tags/$tagName" 2>$null
    if (-not $remoteTag) {
        Write-Err "✗ Remote tag $tagName does not exist."
        Write-Info "  Cannot retry - the release was not started."
        Write-Info "  Run: .\release.ps1 -Version $ReleaseVersion"
        exit 1
    }

    # Check if any packages are already published
    $anyPublished = $false
    foreach ($package in $Packages) {
        if (Test-PackageVersion -Package $package -PackageVersion $ReleaseVersion) {
            $anyPublished = $true
            break
        }
    }

    if ($anyPublished) {
        Write-Err "✗ Some packages are already published for version $ReleaseVersion."
        Write-Info "  Cannot simply retry - need to cleanup first."
        Write-Info "  Run: .\release.ps1 -Cleanup $ReleaseVersion"
        exit 1
    }

    Write-Info ""
    Write-Info "No packages published yet. Re-triggering workflow..."
    Write-Info ""

    # Delete and re-push tag to trigger workflow
    Write-Info "Deleting remote tag $tagName..."
    git push origin --delete $tagName

    Write-Info "Re-pushing tag $tagName..."
    # Make sure we have the local tag
    $localTag = git tag -l $tagName
    if (-not $localTag) {
        # Create tag at current HEAD (should be the version bump commit)
        git tag $tagName
    }
    git push origin $tagName

    Write-Success ""
    Write-Success "✓ Release $ReleaseVersion re-triggered!"
    Write-Success ""
    Write-Info "Monitor the workflow: https://github.com/$GitHubOrg/$GitHubRepo/actions"
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

# Handle Status command
if ($Status) {
    Get-ReleaseStatus -ReleaseVersion $Status
    exit $LASTEXITCODE
}

# Handle Cleanup command
if ($Cleanup) {
    Invoke-ReleaseCleanup -ReleaseVersion $Cleanup
    exit 0
}

# Handle Retry command
if ($Retry) {
    Invoke-ReleaseRetry -ReleaseVersion $Retry
    exit 0
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
    Write-Info "       .\release.ps1 -Status <version>   # Check release status"
    Write-Info "       .\release.ps1 -Cleanup <version>  # Clean up failed release"
    Write-Info "       .\release.ps1 -Retry <version>    # Retry failed release"
    Write-Info ""
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