# Creating a Release

This document explains how to create and publish a new version of MethodCache to GitHub Packages.

## Quick Release (Recommended)

Use the automated release script:

**PowerShell (Windows):**
```powershell
.\release.ps1 -Version 1.0.0
```

**Bash (Linux/Mac):**
```bash
./release.sh 1.0.0
```

The script will:
1. Validate you're on main branch with no uncommitted changes
2. Run all tests
3. Update `version.json` using nbgv
4. Commit the version change
5. Create and push the git tag
6. Trigger GitHub Actions to publish

### Options

Skip tests (not recommended):
```powershell
.\release.ps1 -Version 1.0.0 -SkipTests
```
```bash
./release.sh 1.0.0 --skip-tests
```

## Manual Release Process

If you prefer to do it manually:

1. **Ensure all changes are committed and pushed to main**
   ```bash
   git checkout main
   git pull
   ```

2. **Update version.json**
   ```bash
   dotnet tool restore
   nbgv set-version 1.0.0
   git add version.json
   git commit -m "Bump version to 1.0.0"
   git push
   ```

3. **Create and push a version tag**
   ```bash
   git tag v1.0.0
   git push origin v1.0.0
   ```

4. **Automated publishing**
   - GitHub Actions will automatically detect the tag push
   - The workflow will:
     - Build the project
     - Run all tests (including integration tests with Redis)
     - Pack NuGet packages
     - Publish to GitHub Packages

4. **Monitor the workflow**
   - Visit: https://github.com/eoniclabs/MethodCache/actions
   - Check the "Publish NuGet Package" workflow run
   - Verify successful completion

## Version Tagging

Use semantic versioning for tags:
- `v1.0.0` - Major release (breaking changes)
- `v1.1.0` - Minor release (new features, backward compatible)
- `v1.0.1` - Patch release (bug fixes)

## Using the Package in Other Projects

### 1. Authenticate to GitHub Packages

Create a Personal Access Token (PAT) with `read:packages` scope:
- Go to GitHub Settings → Developer settings → Personal access tokens → Tokens (classic)
- Generate new token with `read:packages` scope
- Copy the token

### 2. Configure NuGet to use GitHub Packages

Add or update your `nuget.config` file in your project:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="github-eoniclabs" value="https://nuget.pkg.github.com/eoniclabs/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <github-eoniclabs>
      <add key="Username" value="YOUR_GITHUB_USERNAME" />
      <add key="ClearTextPassword" value="YOUR_GITHUB_PAT" />
    </github-eoniclabs>
  </packageSourceCredentials>
</configuration>
```

**Alternative: Use environment variables or local config**

```bash
# Using dotnet CLI (stored in user profile)
dotnet nuget add source "https://nuget.pkg.github.com/eoniclabs/index.json" \
  --name github-eoniclabs \
  --username YOUR_GITHUB_USERNAME \
  --password YOUR_GITHUB_PAT \
  --store-password-in-clear-text
```

### 3. Install the package

```bash
dotnet add package MethodCache.Core --version 1.0.0
```

Or in your `.csproj` file:

```xml
<PackageReference Include="MethodCache.Core" Version="1.0.0" />
```

## CI/CD Integration

For CI/CD systems, use the `GITHUB_TOKEN` secret or a dedicated PAT:

```yaml
- name: Restore packages
  run: dotnet restore
  env:
    NUGET_AUTH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```

Add this to your `nuget.config` for CI:

```xml
<packageSourceCredentials>
  <github-eoniclabs>
    <add key="Username" value="github" />
    <add key="ClearTextPassword" value="%NUGET_AUTH_TOKEN%" />
  </github-eoniclabs>
</packageSourceCredentials>
```

## Troubleshooting

**Package not found**
- Verify you have access to the eoniclabs/MethodCache repository
- Check your PAT has `read:packages` scope
- Ensure the package version exists: https://github.com/eoniclabs/MethodCache/packages

**Unauthorized errors**
- Regenerate your PAT
- Verify username/password in nuget.config
- Try clearing NuGet cache: `dotnet nuget locals all --clear`

**Workflow failures**
- Check Redis service is running (for integration tests)
- Verify all tests pass locally: `dotnet test`
- Review GitHub Actions logs for specific errors