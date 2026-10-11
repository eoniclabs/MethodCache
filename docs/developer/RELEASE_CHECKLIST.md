# Release checklist

Work through this before tagging a release. [RELEASE.md](../../RELEASE.md) has the steps that tag and publish it;
publishing to nuget.org needs the maintainer's go-ahead.

## The code

- [ ] `main` is green on the build servers ("MethodCache CI": Linux, Windows and macOS).
- [ ] No open issue is labelled as a bug that a user would hit in this release.
- [ ] The longer checks have run on the release commit (see `build/ci/README.md`):
  - [ ] `PROPERTY_SECONDS=120 build/ci/linux.sh build properties`: the properties from fresh seeds, for longer.
  - [ ] `METHODCACHE_SOAK_SECONDS=1800 build/ci/linux.sh build soak`: 30 minutes under load.
  - [ ] `build/ci/linux.sh build mutation`: read the report, and add tests for surviving mutants in code that changed since the
    last release.
- [ ] The benchmarks still support the numbers in `README.md` and `docs/benchmarks.md`
  (`dotnet run -c Release --project benchmarks/MethodCache.Benchmarks`).

## The packages

- [ ] `build/ci/linux.sh build packages` (it packs them first): all seven
  packages install and work in a fresh app, with the .NET 8 and the current SDK.
- [ ] The version is right for the change (`release.sh` sets it in `version.json`): a breaking change after 1.0 needs
  a new major version.
- [ ] Each package's README (shown on nuget.org) matches what the package does.

## The public API

- [ ] Move every `PublicAPI.Unshipped.txt` entry to `PublicAPI.Shipped.txt` in each project.
- [ ] Move `src/MethodCache.Analyzers/AnalyzerReleases.Unshipped.md` to a new release section in
  `AnalyzerReleases.Shipped.md`.

## The docs

- [ ] `docs/security.md` still describes what the release does, and every threat in it has its test.
- [ ] `llms.txt` and `llms-full.txt` are current (`python3 .github/scripts/generate-llms.py` changes nothing).
- [ ] The release notes list the behavior changes and anything a user must do when upgrading.
