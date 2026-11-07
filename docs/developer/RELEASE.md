# Releasing MethodCache

How to check, tag and publish a release. Releases go to nuget.org, and each one needs the maintainer's go-ahead.

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

- [ ] `build/ci/linux.sh build packages` (it packs them first): all ten packages install and work in a fresh app,
  with the supported .NET 10 SDK and runtime.
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
- [ ] [CHANGELOG.md](../../CHANGELOG.md) has a heading for the new version, `## <version> (<date>)`, with these
  parts in this order, each only when it has entries:
  - `### Upgrading from <previous version>`: what a user must change, new build errors and warnings, and every key
    that is now written differently. For changed keys, say that instances on the two versions miss each other's
    entries during a rolling deploy, and that an invalidation by key reaches only the entries of its own version.
  - `### Added`, `### Changed`, `### Fixed` and `### Removed`.

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

### From 1.0.0: signed tags and immutable releases

- Sign the tag: `git tag -s v1.0.0 -m "MethodCache 1.0.0"` with the maintainer's GPG or SSH signing key
  (`git config gpg.format ssh` and `user.signingkey` for an SSH key). The key must be added to the maintainer's GitHub
  account as a signing key, so GitHub shows the tag as Verified. `release.sh` and `release.ps1` create a plain tag:
  for 1.0.0 and later, tag by hand as shown above, with `-s`.
- Before publishing 1.0.0, turn on immutable releases in the repository settings (Settings, General, Releases). A
  published release's tag and assets then can't be changed or deleted, so check the release text before publishing
  it: create it as a draft (`gh release create ... --draft`), read it, then publish it.

## Publish it

1. **Wait for the build servers.** The tagged commit is the head of `main`, so the coordinator runs every CI job on
   it within minutes. Wait until its `MethodCache CI` commit status is green. The coordinator only tests the head of
   `main` and open pull requests, so do not merge anything until then. If another commit landed first, the tagged
   commit is never tested: push a branch at the tag (`git push origin v1.0.0:refs/heads/release-1.0.0`) and open a
   draft pull request from it, so the coordinator tests that commit, then close the pull request and delete the
   branch.
2. **Publish on Megatron after the maintainer approves the public release:**

   ```bash
   build/ci/publish.sh v1.0.0
   ```

   It refuses a tag whose `MethodCache CI` status is not green. It packs the 10 packages from a fresh clone of the tag
   and pushes them and their symbol packages to nuget.org using BWS (see the configuration below).
   Running it again is safe: versions already published are skipped
   (`./release.sh --retry <version>` prints the command again).
3. **Check all ten packages** on nuget.org after indexing: versions, `eoniclabs` ownership and available
   symbols. Do this for `rc.1` before advancing the release. Verify symbol-server retrieval of the portable
   PDBs; an accepted upload alone does not prove symbol indexing succeeded.
4. **Write the GitHub release** for the tag, titled `MethodCache <version>` (such as `MethodCache 1.0.0-rc.11`), with
   the version's section from [CHANGELOG.md](../../CHANGELOG.md) as its text, and marked as a pre-release for an rc:

   ```bash
   gh release create v1.0.0-rc.11 --title "MethodCache 1.0.0-rc.11" --notes-file notes.md --prerelease
   ```

`./release.sh --status <version>` shows the tag, its `MethodCache CI` status and which packages nuget.org has
indexed. `./release.sh --cleanup <version>` deletes the tag and reverts the version commit of a release that
published nothing; it refuses once a package is on nuget.org, where versions can't be deleted, only unlisted.

### When publishing fails

- `MethodCache CI` is not green: the build servers' comment and logs say which job failed (see
  [build/ci/README.md](../../build/ci/README.md)).
- "Missing <package>": `version.json` at the tag does not match the tag's version.
- NuGet unauthorized: renew the scoped API key in NuGet and BWS.

### NuGet credentials and symbols

`build/ci/publish.sh v1.0.0` publishes to nuget.org (`--nuget` is still accepted). It checks the
`MethodCache CI` status, ten package versions and their ten `.snupkg` companions, then retrieves `METHODCACHE_NUGET_ORG_API_KEY` from
Bitwarden Secrets Manager (BWS) and pushes to `https://api.nuget.org/v3/index.json` with `--skip-duplicate`.

Configure `METHODCACHE_NUGET_SECRET_ID` with the BWS secret UUID, or put that UUID alone in
`~/.config/methodcache-publish/nuget-secret-id`. These are references, never API-key values. BWS uses the
existing `BWS_ACCESS_TOKEN` or the token file at `${BWS_TOKEN_FILE:-$HOME/.bws_token}`, and its configured
region (`BWS_SERVER_URL` can override it). Scope the machine account to the publishing project when the
Bitwarden plan permits a separate project.

NuGet publishing requires a stable .NET SDK 10.0.300 or later, which supports `NUGET_API_KEY` and
`NUGET_SYMBOL_API_KEY` in the process environment. Set `METHODCACHE_PUBLISH_DOTNET` to its absolute executable path. By default the script uses
`~/.local/share/methodcache-publish-dotnet/dotnet` when installed, otherwise `dotnet`. Packing still uses the
usual SDK and the release's `global.json`; the publishing CLI runs outside that clone. The script reads the
NuGet key after packing, disables shell tracing, and never passes it as a command-line argument. Build steps
do not inherit `BWS_ACCESS_TOKEN`, `NUGET_API_KEY` or `NUGET_SYMBOL_API_KEY`. This is not a sandbox: publishing must run only trusted
release code on a trusted host.

The publisher sets both NuGet environment variables to the same scoped BWS key. NuGet discovers its symbol
endpoint from the feed and automatically uploads each neighboring `.snupkg`. Keys are never passed through
`--api-key` or `--symbol-api-key`. Run the real SDK transport check with
`METHODCACHE_TEST_PUBLISH_DOTNET=/absolute/path/to/dotnet python3 -m unittest discover -s build/ci/tests -p test_publish_sdk.py -v`.
It verifies both symbol-key fallback and the explicit symbol environment variable against a loopback feed
using synthetic packages and keys. It does not verify NuGet.org symbol indexing.

New NuGet API keys expire after at most 30 days. Create a successor with owner `eoniclabs`, scope
**Push new packages and package versions**, glob `MethodCache*`, and no unlist permission. Save its value
directly to BWS through the web app or SDK, update the secret reference, and verify retrieval before retiring
the previous key. Never paste a token into logs, shell arguments, or Git. A future migration to Trusted
Publishing can remove recurring key rotation when the publishing platform supports it.

After uploading, check all ten packages, their versions and their `eoniclabs` ownership on nuget.org once
indexing completes; `./release.sh --status <version>` lists which ones nuget.org has.

For this destination, missing `bws` or `jq` produces a named prerequisite error. An old SDK or
missing/incorrect BWS reference stops before publication; an empty secret or BWS authentication error also
stops every upload. An expired NuGet key requires renewal in NuGet
and BWS, rather than `gh auth refresh`.

## Using a pre-release build

Release candidates are on nuget.org like any release. Ask for the version explicitly, or allow pre-releases:

```bash
dotnet add package MethodCache --version 1.0.0-rc.11
```

GitHub Packages no longer has MethodCache packages. Remove a `nuget.pkg.github.com/eoniclabs` source you added
for them (`dotnet nuget remove source github-eoniclabs`).
