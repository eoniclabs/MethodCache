# Releasing MethodCache

How to check, tag and publish a release. Releases go to GitHub Packages only. Nothing is published to nuget.org yet;
that needs the maintainer's go-ahead.

## Before you tag

### The code

- [ ] `main` is green on the build servers ("MethodCache CI": Linux, Windows and macOS).
- [ ] No open issue is labelled as a bug that a user would hit in this release.
- [ ] The longer checks have run on the release commit (see [build/ci/README.md](../../build/ci/README.md)):
  - [ ] `PROPERTY_SECONDS=120 build/ci/linux.sh build properties`: the properties from fresh seeds, for longer.
  - [ ] `METHODCACHE_SOAK_SECONDS=1800 build/ci/linux.sh build soak`: 30 minutes under load.
  - [ ] `build/ci/linux.sh build mutation`: read the report, and add tests for surviving mutants in code that
    changed since the last release.
- [ ] The benchmarks still support the numbers in `README.md` and `docs/benchmarks.md`
  (`dotnet run -c Release --project benchmarks/MethodCache.Benchmarks -- readme --filter '*Hit'`, as
  [docs/benchmarks.md](../benchmarks.md) describes).

### The packages

- [ ] `build/ci/linux.sh build packages` (it packs them first): all seven packages install and work in a fresh app,
  with the .NET 8 and the current SDK.
- [ ] The version is right for the change (`release.sh` sets it in `version.json`): a breaking change after 1.0
  needs a new major version.
- [ ] Each package's README (shown on the package page) matches what the package does.

### The public API

- [ ] Move every `PublicAPI.Unshipped.txt` entry to `PublicAPI.Shipped.txt` in each project.
- [ ] Move `src/MethodCache.Analyzers/AnalyzerReleases.Unshipped.md` to a new release section in
  `AnalyzerReleases.Shipped.md`.

### The docs

- [ ] `docs/security.md` still describes what the release does, and every threat in it has its test.
- [ ] `llms.txt` and `llms-full.txt` are current (`python3 .github/scripts/generate-llms.py` changes nothing).
- [ ] [CHANGELOG.md](../../CHANGELOG.md) lists the behavior changes and anything a user must do when upgrading,
  under a heading for the new version.

## Tag the release

Run the release script from an up-to-date `main` with no uncommitted changes:

```bash
./release.sh 1.0.0                # Linux and macOS
.\release.ps1 -Version 1.0.0      # Windows
```

It checks that you are on `main` with a clean working tree, runs the tests (`--skip-tests` or `-SkipTests` skips
them), sets the version in `version.json` with `dotnet nbgv set-version`, commits and pushes that change, and creates
and pushes the tag `v<version>`. Run it without a version to see suggested version numbers.

To do the same by hand:

```bash
git checkout main
git pull
dotnet tool restore
dotnet nbgv set-version 1.0.0
git add version.json
git commit -m "Bump version to 1.0.0"
git push
git tag v1.0.0
git push origin v1.0.0
```

Use semantic versioning: a new major version for breaking changes, a minor version for new features and a patch
version for bug fixes.

Tags from before the first public release (v1.0.0–v1.2.3, 2025) were deleted on 2026-10-13; the first release is 1.0.0.

## Publish it

1. **Wait for the build servers.** The tagged commit is the head of `main`, so the coordinator runs every CI job on
   it within minutes. Wait until its `MethodCache CI` commit status is green. The coordinator only tests the head of
   `main` and open pull requests, so do not merge anything until then. If another commit landed first, the tagged
   commit is never tested: push a branch at the tag (`git push origin v1.0.0:refs/heads/release-1.0.0`) and open a
   draft pull request from it, so the coordinator tests that commit, then close the pull request and delete the
   branch.
2. **Publish on Megatron:**

   ```bash
   build/ci/publish.sh v1.0.0
   ```

   It refuses a tag whose `MethodCache CI` status is not green. It packs the 7 packages from a fresh clone of the tag
   and pushes them to GitHub Packages. `gh` needs the `write:packages` scope once:
   `gh auth refresh -s write:packages`. Running it again is safe: versions already published are skipped
   (`./release.sh --retry <version>` prints the command again).
3. **Check the packages** at https://github.com/eoniclabs/MethodCache/packages.
4. **Write the GitHub release** for the tag, with the version's section from [CHANGELOG.md](../../CHANGELOG.md).

`./release.sh --status <version>` shows whether a release was published, and `./release.sh --cleanup <version>`
cleans up a failed one: it deletes the packages and the tag, and reverts the version commit.

### When publishing fails

- `MethodCache CI` is not green: the build servers' comment and logs say which job failed (see
  [build/ci/README.md](../../build/ci/README.md)).
- "Missing <package>": `version.json` at the tag does not match the tag's version.
- Unauthorized: run `gh auth refresh -s write:packages`.

## Using a pre-release build

Pre-release builds are shared through GitHub Packages, which needs a GitHub token even to read. Create a personal
access token (classic) with the `read:packages` scope, then add the feed once:

```bash
dotnet nuget add source "https://nuget.pkg.github.com/eoniclabs/index.json" \
  --name github-eoniclabs \
  --username YOUR_GITHUB_USERNAME \
  --password YOUR_GITHUB_PAT \
  --store-password-in-clear-text

dotnet add package MethodCache --version 1.0.0
```

A "package not found" or "unauthorized" error usually means the token lacks `read:packages` or the version does
not exist on the [packages page](https://github.com/eoniclabs/MethodCache/packages). `dotnet nuget locals all --clear`
clears a stale cache.
