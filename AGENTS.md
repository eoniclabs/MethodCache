# Repository Guidelines

Read this before changing the repository. It is written for human contributors and coding agents alike.

## Project Structure & Modules
- `src/`: the shipped libraries.
  - `MethodCache.Core`: Core APIs, attributes, in-memory cache. Packs as the `MethodCache` package, which also carries the generator and analyzers.
  - `MethodCache.SourceGenerator`: Roslyn source generator for decorators (shipped inside `MethodCache`).
  - `MethodCache.Analyzers`: Compile-time checks and diagnostics (shipped inside `MethodCache`).
  - `MethodCache.Redis`, `MethodCache.SqlServer`: distributed cache (L2) with a backplane.
  - `MethodCache.Serialization.MessagePack`, `MethodCache.AspNetCore`, `MethodCache.OpenTelemetry`, `MethodCache.HttpCaching`: optional add-ons.
- `tests/`: xUnit unit tests (`*.Tests`), Docker-backed integration tests (`*.IntegrationTests`), and three CI-only programs: `MethodCache.AotSmokeTest` (Native AOT), `MethodCache.PackageSmokeTest` (installs the packed `MethodCache` package) and `MethodCache.PackageSmokeTest.AddOns` (installs the six add-on packages and uses each one in an app, with Redis and SQL Server from `METHODCACHE_REDIS_URL` and `METHODCACHE_SQLSERVER_CONNECTION`). Both package tests read `artifacts/packages`; run `dotnet pack MethodCache.sln -c Release -o artifacts/packages` first.
- `tests/MethodCache.Docs.Tests` holds every C# example shown in the docs (`README.md`, `NUGET_README.md`, the package READMEs and `docs/`), so they compile, and tests for the claims the docs make (`ReadmeTests`, `FeatureTests`). It also runs the console/web demo scenarios, including Docker-backed Redis tests (`Category=RedisDemo`); the `unit` CI job excludes that category.
- `samples/`: `MethodCache.Demo` (an eleven-chapter console tour including HttpClient caching, run in CI) and `MethodCache.WebDemo` (ASP.NET Core).
- `benchmarks/MethodCache.Benchmarks`: Performance benchmarks.

## Build, Test, Run
- Restore: `dotnet restore`
- Build: `dotnet build MethodCache.sln -c Release`
- Unit tests: `dotnet test MethodCache.sln -c Release`
- Coverage (to `test-results/`): `dotnet test --collect:"XPlat Code Coverage" --results-directory test-results`
- Integration tests (Docker or `METHODCACHE_REDIS_URL`; see `docs/developer/INTEGRATION_TEST_STRATEGY.md`): `dotnet test tests/MethodCache.Redis.IntegrationTests`
- Demo: `dotnet run --project samples/MethodCache.Demo -c Release`
- Automated demos (after a Release build): `bash build/ci/jobs.sh demo`; `bash build/ci/jobs.sh webdemo` adds the Docker-backed Redis scenarios.
- CI: every CI job is a script in `build/ci` (see `build/ci/README.md`). `build/ci/linux.sh` runs all Linux jobs in the build image with Docker, exactly as the build server does; `build/ci/jobs.sh <job>` runs one job with your local SDKs.

## Coding Style & Naming
- Language: C# (SDK pinned via `global.json`), 4-space indent.
- Naming: PascalCase for types/methods; camelCase for locals/params; `_camelCase` for private fields; async methods end with `Async`.
- Patterns: Prefer DI, immutability where practical, early returns; avoid sync-over-async.
- Formatting/linting: Use built-in analyzers plus project analyzers; run `dotnet format` before PRs.

## Testing Guidelines
- Framework: xUnit. Place tests in `*.Tests` projects; files end with `*Tests.cs`.
- Style: Arrange/Act/Assert, deterministic tests; name methods like `Method_State_Expected`.
- Coverage: Aim for meaningful coverage on new/changed code; include negative and boundary cases.
- Bug fixes start with a failing test. Write the test that shows the bug, see it fail, then fix it, and say in the pull request that the test failed before the fix. A bug found any other way means a kind of test is missing: add it where it belongs.
- Where each kind of test lives:
  - A new method shape the generator must handle: a case in `tests/MethodCache.SourceGenerator.IntegrationTests/Tests/SignatureCorpusTests.cs`.
  - Anything that depends on time (expiry, refresh, timeouts): a registered `FakeTimeProvider`, never `Task.Delay` or sleeps.
  - Rules that must hold for every input (keys, tags, the memory engine against a model): CsCheck properties in `tests/MethodCache.Core.Tests/Properties`.
  - A distributed cache or backplane that hangs, fails or loses messages: `tests/MethodCache.Core.Tests/FaultInjection`.
  - Isolation between users, authorization, telemetry contents and untrusted input: the security tests, with the threat list in `docs/security.md` kept in step.
  - Behavior the docs promise: a test in `tests/MethodCache.Docs.Tests`.
- Integration: see `docs/developer/INTEGRATION_TEST_STRATEGY.md` for where the Redis and SQL Server come from.

## Commits & Pull Requests
- Commits: Imperative present tense (e.g., "Refactor X", "Add Y"); small, focused; include rationale in body when helpful.
- PRs: fill in `.github/pull_request_template.md`: what a user sees before and after, how it works, and how it was tested. Include docs updates (README or samples) when behavior changes. Add screenshots only if UI/dev tooling is affected.
- Releases: follow `docs/developer/RELEASE.md` (the checklist, tagging and publishing). User-visible changes go into `CHANGELOG.md`.

## Security & Configuration
- Do not log sensitive data or include secrets in cache keys. Use environment variables or user-secrets for Redis connection strings.
- Prefer dependency injection over statics; validate options (`IOptions<T>`) and fail fast on misconfiguration.

## Rules that are easy to miss
- Raw measurements, profiles, per-launch logs, generated reports and evidence catalogues belong under ignored `artifacts/` or outside the checkout. Never force-add them. Keep benchmark source, approved limits and short result/decision summaries in Git.
- Public API is locked by PublicApiAnalyzers. Any added, removed or changed public member must be recorded in that project's `PublicAPI.Unshipped.txt`, or the build fails.
- Every public type and member in `src/` needs an XML doc comment (`CS1591` is an error). Write what it does for a user, and use `<inheritdoc />` on interface implementations and overrides.
- Nothing is on nuget.org yet, so an API that is replaced is deleted outright. Do not add `[Obsolete]` shims.
- Library code must use `ConfigureAwait(false)` on every await (`CA2007` is an error in `src/`).
- New or changed diagnostics need a row in `docs/user-guide/DIAGNOSTICS.md` and an entry in `src/MethodCache.Analyzers/AnalyzerReleases.Unshipped.md` when they come from the analyzers.
- C# examples in Markdown are not written by hand. They live in `tests/MethodCache.Docs.Tests` between `// begin-snippet: name` and `// end-snippet`, a `.md` file references them with a line `snippet: name`, and `dotnet tool restore && dotnet mdsnippets` copies them in (CI fails when the docs are out of date). Edit the `.cs` file, never the code between `<!-- snippet: -->` and `<!-- endSnippet -->`.
- `llms.txt` and `llms-full.txt` are generated from the pages `docs/README.md` links: after changing docs, run `python3 .github/scripts/generate-llms.py` (CI fails when they are out of date). Keep `docs/ai/methodcache.md` in step with the rules it states.
- Behavior changes need the user docs updated in the same pull request: `README.md`, the feature page under `docs/features/` (indexed in `docs/README.md`), and the package's NuGet README. For `MethodCache` (`src/MethodCache.Core`) that is `NUGET_README.md` at the repository root; the add-on packages use their own `src/*/README.md`.
- Libraries and test projects target net10.0 only (set in `tests/Directory.Build.props`; smoke/docs projects declare the same target explicitly). Use the .NET 10 SDK; `dotnet test` runs each test once. CI also runs the unit tests on Windows and under the tr-TR and sv-SE cultures, and requires the Docker-backed Redis and SQL Server tests (`METHODCACHE_REQUIRE_INFRA_TESTS=true`) instead of skipping them.
- CI also runs the demo, a Native AOT publish of `tests/MethodCache.AotSmokeTest`, and the package smoke test on .NET 10. Run the ones your change can affect before pushing (`build/ci/linux.sh build demo aot packages`).
- Diff coverage: a pull request fails when tests run less than 80% of the lines it adds or changes under `src/` (`build/ci/jobs.sh build diffcoverage` runs the net10.0 tests with coverage and lists the uncovered lines). Test the code you add.
- CI also runs the CsCheck properties from random seeds (`properties`) and a short soak test (`soak`) on every pull request. Stryker.NET mutation testing on `MethodCache.Core` (`mutation`, a report, never a failure) takes about an hour and is started by hand; see `build/ci/README.md`. The soak tests (`[SoakFact]`, `Category=Soak`) are skipped unless `METHODCACHE_SOAK_SECONDS` is set, and the property tests keep their fixed seed in every other run, so the normal test run stays deterministic.
- A new CI job goes into `build/ci/jobs.sh` (or `build/ci/windows.ps1`) as a script or `dotnet` command. There is no GitHub Actions: our build servers run these scripts and post the `MethodCache CI` commit status, which is the merge gate (see `build/ci/README.md`).

## Developer docs
- `docs/developer/design-spec.md`: design target and acceptance cases for the pre-1.0 implementation PRs; describes planned behavior, not the current API.
- `docs/developer/RELEASE.md`: release checklist, tagging and publishing.
- `docs/developer/INTEGRATION_TEST_STRATEGY.md`: the Docker-backed integration tests.
- `docs/developer/RUNTIME_CONFIGURATION_OVERVIEW.md`: how policies are resolved from their sources and changed at runtime.
- `build/ci/README.md`: the CI jobs and build servers.
- `benchmarks/MethodCache.Benchmarks/README.md`: which benchmark to run and how.

Tip: When adding a new module, follow the `MethodCache.*` naming, add it to `MethodCache.sln`, and provide minimal tests and a README or example usage.
