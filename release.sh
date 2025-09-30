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

# Check if version parameter is provided
if [ -z "$1" ]; then
    print_error "❌ Version number required"
    echo "Usage: ./release.sh <version> [--skip-tests]"
    echo "Example: ./release.sh 1.0.0"
    exit 1
fi

VERSION=$1
SKIP_TESTS=false

# Check for --skip-tests flag
if [ "$2" == "--skip-tests" ]; then
    SKIP_TESTS=true
fi

# Validate version format
if ! [[ $VERSION =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[a-zA-Z0-9\-\.]+)?$ ]]; then
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
nbgv set-version $VERSION

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