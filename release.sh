#!/bin/bash
set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
CYAN='\033[0;36m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

function print_success { echo -e "${GREEN}$1${NC}"; }
function print_info { echo -e "${CYAN}$1${NC}"; }
function print_warning { echo -e "${YELLOW}$1${NC}"; }
function print_error { echo -e "${RED}$1${NC}"; }

# GitHub organization/owner
GITHUB_ORG="eoniclabs"
GITHUB_REPO="MethodCache"

# Package names to check/manage
PACKAGES=(
    "MethodCache"
    "MethodCache.Abstractions"
    "MethodCache.Analyzers"
    "MethodCache.Core"
    "MethodCache.ETags"
    "MethodCache.Providers.Redis"
    "MethodCache.SourceGenerator"
)

# Function to get current version from version.json
get_current_version() {
    if [ ! -f "version.json" ]; then
        echo "1.0.0"
        return
    fi
    # Extract version using grep and sed (works on macOS without jq)
    grep '"version"' version.json | sed 's/.*"version".*:.*"\([^"]*\)".*/\1/'
}

# Function to check if a package version exists on GitHub Packages
check_package_version() {
    local package=$1
    local version=$2

    # Use gh api to check if version exists
    local result=$(gh api "/orgs/$GITHUB_ORG/packages/nuget/$package/versions" 2>/dev/null | grep -o "\"name\":\"$version\"" || true)

    if [ -n "$result" ]; then
        return 0  # Version exists
    else
        return 1  # Version does not exist
    fi
}

# Function to check release status
check_release_status() {
    local version=$1
    local tag_name="v$version"

    print_info "Checking release status for version $version..."
    print_info ""

    # Check if tag exists locally
    local local_tag=$(git tag -l "$tag_name")
    if [ -n "$local_tag" ]; then
        print_success "✓ Local tag $tag_name exists"
    else
        print_warning "✗ Local tag $tag_name does not exist"
    fi

    # Check if tag exists on remote
    local remote_tag=$(git ls-remote --tags origin "refs/tags/$tag_name" 2>/dev/null)
    if [ -n "$remote_tag" ]; then
        print_success "✓ Remote tag $tag_name exists"
    else
        print_warning "✗ Remote tag $tag_name does not exist"
    fi

    # Check GitHub Actions workflow status
    print_info ""
    print_info "Checking workflow runs for tag $tag_name..."
    local workflow_status=$(gh run list --workflow=publish.yml --limit=5 --json headBranch,status,conclusion,url 2>/dev/null || echo "[]")

    # Find run for this tag
    local run_for_tag=$(echo "$workflow_status" | grep -o "{[^}]*\"headBranch\":\"$tag_name\"[^}]*}" || true)
    if [ -n "$run_for_tag" ]; then
        local status=$(echo "$run_for_tag" | grep -o '"status":"[^"]*"' | cut -d'"' -f4)
        local conclusion=$(echo "$run_for_tag" | grep -o '"conclusion":"[^"]*"' | cut -d'"' -f4)
        local url=$(echo "$run_for_tag" | grep -o '"url":"[^"]*"' | cut -d'"' -f4)

        if [ "$status" = "completed" ]; then
            if [ "$conclusion" = "success" ]; then
                print_success "✓ Workflow completed successfully"
            else
                print_error "✗ Workflow failed (conclusion: $conclusion)"
                print_info "  View details: $url"
            fi
        else
            print_warning "⏳ Workflow still running (status: $status)"
            print_info "  View details: $url"
        fi
    else
        print_warning "✗ No workflow run found for tag $tag_name"
    fi

    # Check published packages
    print_info ""
    print_info "Checking published packages..."
    local all_published=true
    local any_published=false

    for package in "${PACKAGES[@]}"; do
        if check_package_version "$package" "$version"; then
            print_success "  ✓ $package@$version published"
            any_published=true
        else
            print_warning "  ✗ $package@$version NOT published"
            all_published=false
        fi
    done

    print_info ""
    if [ "$all_published" = true ]; then
        print_success "✓ All packages published successfully!"
        return 0
    elif [ "$any_published" = true ]; then
        print_error "⚠ PARTIAL RELEASE: Some packages were published, some were not."
        print_info "  This indicates a failed release that needs cleanup."
        print_info "  Run: ./release.sh --cleanup $version"
        return 2
    else
        print_warning "✗ No packages published for version $version"
        print_info "  The release may have failed before publishing."
        print_info "  Run: ./release.sh --retry $version"
        return 1
    fi
}

# Function to delete a package version from GitHub Packages
delete_package_version() {
    local package=$1
    local version=$2

    # Get version ID
    local version_id=$(gh api "/orgs/$GITHUB_ORG/packages/nuget/$package/versions" 2>/dev/null | \
        grep -B5 "\"name\":\"$version\"" | grep -o '"id":[0-9]*' | head -1 | cut -d':' -f2)

    if [ -n "$version_id" ]; then
        print_info "  Deleting $package@$version (ID: $version_id)..."
        if gh api --method DELETE "/orgs/$GITHUB_ORG/packages/nuget/$package/versions/$version_id" 2>/dev/null; then
            print_success "  ✓ Deleted $package@$version"
            return 0
        else
            print_error "  ✗ Failed to delete $package@$version"
            return 1
        fi
    else
        print_info "  $package@$version not found, skipping"
        return 0
    fi
}

# Function to cleanup a failed release
cleanup_release() {
    local version=$1
    local tag_name="v$version"

    print_warning "⚠ This will delete all traces of version $version"
    print_warning "  - Delete published packages from GitHub Packages"
    print_warning "  - Delete local and remote tags"
    print_warning "  - Revert version.json commit (if it's the HEAD)"
    print_info ""
    read -p "Are you sure you want to continue? (y/N) " -n 1 -r
    echo

    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        print_info "Cleanup cancelled."
        exit 0
    fi

    print_info ""
    print_info "Starting cleanup for version $version..."

    # Delete published packages
    print_info ""
    print_info "Deleting published packages..."
    for package in "${PACKAGES[@]}"; do
        delete_package_version "$package" "$version"
    done

    # Delete remote tag
    print_info ""
    print_info "Deleting remote tag..."
    if git ls-remote --tags origin "refs/tags/$tag_name" 2>/dev/null | grep -q "$tag_name"; then
        if git push origin --delete "$tag_name" 2>/dev/null; then
            print_success "✓ Deleted remote tag $tag_name"
        else
            print_error "✗ Failed to delete remote tag $tag_name"
        fi
    else
        print_info "Remote tag $tag_name does not exist, skipping"
    fi

    # Delete local tag
    print_info ""
    print_info "Deleting local tag..."
    if git tag -l "$tag_name" | grep -q "$tag_name"; then
        git tag -d "$tag_name"
        print_success "✓ Deleted local tag $tag_name"
    else
        print_info "Local tag $tag_name does not exist, skipping"
    fi

    # Check if we should revert the version commit
    print_info ""
    print_info "Checking version.json commit..."
    local head_message=$(git log -1 --format=%s)
    if [[ "$head_message" == "Bump version to $version" ]]; then
        print_warning "HEAD commit is the version bump. Reverting..."
        git revert --no-commit HEAD
        git commit -m "Revert version bump to $version (release failed)"
        git push
        print_success "✓ Reverted version commit"
    else
        print_info "HEAD commit is not the version bump, skipping revert"
    fi

    print_info ""
    print_success "✓ Cleanup complete!"
    print_info ""
    print_info "You can now retry the release with:"
    print_info "  ./release.sh $version"
}

# Function to retry a failed release (re-trigger workflow)
retry_release() {
    local version=$1
    local tag_name="v$version"

    print_info "Checking if we can retry release $version..."

    # Check if tag exists on remote
    if ! git ls-remote --tags origin "refs/tags/$tag_name" 2>/dev/null | grep -q "$tag_name"; then
        print_error "✗ Remote tag $tag_name does not exist."
        print_info "  Cannot retry - the release was not started."
        print_info "  Run: ./release.sh $version"
        exit 1
    fi

    # Check if any packages are already published
    local any_published=false
    for package in "${PACKAGES[@]}"; do
        if check_package_version "$package" "$version"; then
            any_published=true
            break
        fi
    done

    if [ "$any_published" = true ]; then
        print_error "✗ Some packages are already published for version $version."
        print_info "  Cannot simply retry - need to cleanup first."
        print_info "  Run: ./release.sh --cleanup $version"
        exit 1
    fi

    print_info ""
    print_info "No packages published yet. Re-triggering workflow..."
    print_info ""

    # Delete and re-push tag to trigger workflow
    print_info "Deleting remote tag $tag_name..."
    git push origin --delete "$tag_name"

    print_info "Re-pushing tag $tag_name..."
    # Make sure we have the local tag
    if ! git tag -l "$tag_name" | grep -q "$tag_name"; then
        # Create tag at current HEAD (should be the version bump commit)
        git tag "$tag_name"
    fi
    git push origin "$tag_name"

    print_success ""
    print_success "✓ Release $version re-triggered!"
    print_success ""
    print_info "Monitor the workflow: https://github.com/$GITHUB_ORG/$GITHUB_REPO/actions"
}

# Function to suggest next versions
suggest_versions() {
    local current=$1

    # Parse version (handles both X.Y and X.Y.Z, with or without prerelease)
    if [[ $current =~ ^([0-9]+)\.([0-9]+)(\.([0-9]+))?(-(.+))?$ ]]; then
        local major="${BASH_REMATCH[1]}"
        local minor="${BASH_REMATCH[2]}"
        local patch="${BASH_REMATCH[4]:-0}"
        local prerelease="${BASH_REMATCH[6]}"

        print_info "Current version: $current"
        print_info ""
        print_info "Suggested versions:"

        if [ -n "$prerelease" ]; then
            # If prerelease, suggest stable
            print_info "  Stable : $major.$minor.$patch"
        else
            # Suggest next versions
            print_info "  Patch  : $major.$minor.$((patch + 1))"
            print_info "  Minor  : $major.$((minor + 1)).0"
            print_info "  Major  : $((major + 1)).0.0"
            print_info "  Alpha  : $major.$((minor + 1)).0-alpha"
            print_info "  Beta   : $major.$((minor + 1)).0-beta"
        fi
    fi
}

# Parse arguments
ACTION="release"
VERSION=""
SKIP_TESTS=false

while [[ $# -gt 0 ]]; do
    case $1 in
        --status)
            ACTION="status"
            VERSION="$2"
            shift 2
            ;;
        --cleanup)
            ACTION="cleanup"
            VERSION="$2"
            shift 2
            ;;
        --retry)
            ACTION="retry"
            VERSION="$2"
            shift 2
            ;;
        --skip-tests)
            SKIP_TESTS=true
            shift
            ;;
        -h|--help)
            ACTION="help"
            shift
            ;;
        *)
            if [ -z "$VERSION" ]; then
                VERSION="$1"
            fi
            shift
            ;;
    esac
done

# Show help
show_help() {
    CURRENT_VERSION=$(get_current_version)
    suggest_versions "$CURRENT_VERSION"
    echo ""
    print_info "Usage: ./release.sh <version> [options]"
    print_info "       ./release.sh --status <version>"
    print_info "       ./release.sh --cleanup <version>"
    print_info "       ./release.sh --retry <version>"
    echo ""
    print_info "Commands:"
    print_info "  <version>            Create a new release"
    print_info "  --status <version>   Check the status of a release"
    print_info "  --cleanup <version>  Clean up a failed release (delete packages, tags)"
    print_info "  --retry <version>    Retry a failed release (re-trigger workflow)"
    echo ""
    print_info "Options:"
    print_info "  --skip-tests         Skip running tests before release"
    print_info "  -h, --help           Show this help message"
    echo ""
    print_info "Examples:"
    print_info "  ./release.sh 1.2.0              # Create release 1.2.0"
    print_info "  ./release.sh --status 1.2.0    # Check if 1.2.0 was published"
    print_info "  ./release.sh --retry 1.2.0     # Re-trigger failed 1.2.0 release"
    print_info "  ./release.sh --cleanup 1.2.0   # Clean up failed 1.2.0 release"
}

# Handle actions
case $ACTION in
    help)
        show_help
        exit 0
        ;;
    status)
        if [ -z "$VERSION" ]; then
            print_error "❌ Version required for --status"
            print_info "Usage: ./release.sh --status <version>"
            exit 1
        fi
        check_release_status "$VERSION"
        exit $?
        ;;
    cleanup)
        if [ -z "$VERSION" ]; then
            print_error "❌ Version required for --cleanup"
            print_info "Usage: ./release.sh --cleanup <version>"
            exit 1
        fi
        cleanup_release "$VERSION"
        exit 0
        ;;
    retry)
        if [ -z "$VERSION" ]; then
            print_error "❌ Version required for --retry"
            print_info "Usage: ./release.sh --retry <version>"
            exit 1
        fi
        retry_release "$VERSION"
        exit 0
        ;;
esac

# Check if version parameter is provided for release
if [ -z "$VERSION" ]; then
    show_help
    exit 0
fi

# Validate version format
if ! [[ $VERSION =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[a-zA-Z0-9.-]+)?$ ]]; then
    print_error "❌ Invalid version format: $VERSION"
    echo "Expected format: 1.0.0 or 1.2.3-beta"
    exit 1
fi

# Ensure we're on main branch
print_info "Checking current branch..."
CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD)
if [ "$CURRENT_BRANCH" != "main" ]; then
    print_error "❌ You must be on the 'main' branch to create a release. Current branch: $CURRENT_BRANCH"
    exit 1
fi

# Ensure working directory is clean
print_info "Checking working directory status..."
if [ -n "$(git status --porcelain)" ]; then
    print_error "❌ Working directory has uncommitted changes. Please commit or stash them first."
    git status --short
    exit 1
fi

# Pull latest changes
print_info "Pulling latest changes..."
git pull

# Check if tag already exists
TAG_NAME="v$VERSION"
if git rev-parse "$TAG_NAME" >/dev/null 2>&1; then
    print_error "❌ Tag $TAG_NAME already exists. Use a different version number."
    exit 1
fi

# Run tests unless skipped
if [ "$SKIP_TESTS" = false ]; then
    print_info "Running tests..."
    dotnet test --configuration Release
    print_success "✓ All tests passed"
fi

# Update version using nbgv
print_info "Updating version.json to $VERSION..."
dotnet tool restore
dotnet nbgv set-version $VERSION

# Commit version change
print_info "Committing version change..."
git add version.json
git commit -m "Bump version to $VERSION"

# Push commit
print_info "Pushing commit to remote..."
git push

# Create and push tag
print_info "Creating tag $TAG_NAME..."
git tag $TAG_NAME

print_info "Pushing tag $TAG_NAME..."
if ! git push origin $TAG_NAME; then
    print_error "❌ Failed to push tag"
    # Cleanup: delete local tag
    git tag -d $TAG_NAME
    exit 1
fi

print_success ""
print_success "✓ Release $VERSION created successfully!"
print_success ""
print_info "Next steps:"
print_info "1. Monitor the workflow: https://github.com/eoniclabs/MethodCache/actions"
print_info "2. Verify packages: https://github.com/eoniclabs/MethodCache/packages"
print_info "3. Update release notes: https://github.com/eoniclabs/MethodCache/releases/tag/$TAG_NAME"
print_success ""