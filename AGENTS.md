# Repository Guidelines

Read this before changing the repository. It is written for human contributors and coding agents alike.

## Project Structure & Modules
- `src/`: the shipped libraries.
  - `MethodCache.Core`: Core APIs, attributes, in-memory cache. Packs as the `MethodCache` package, which also carries the generator and analyzers.
  - `MethodCache.SourceGenerator`: Roslyn source generator for decorators (shipped inside `MethodCache`).
  - `MethodCache.Analyzers`: Compile-time checks and diagnostics (shipped inside `MethodCache`).
  - `MethodCache.Redis`, `MethodCache.SqlServer`: distributed cache (L2) with a backplane.
  - `MethodCache.Serialization.MessagePack`, `MethodCache.AspNetCore`, `MethodCache.OpenTelemetry`, `MethodCache.HttpCaching`: optional add-ons.
- `tests/`: xUnit unit tests (`*.Tests`), Docker-backed integration tests (`*.IntegrationTests`), and two CI-only programs: `MethodCache.AotSmokeTest` (Native AOT) and `MethodCache.PackageSmokeTest` (installs the packed `MethodCache` package from `artifacts/packages`; run `dotnet pack src/MethodCache.Core -c Release -o artifacts/packages` first).
- `tests/MethodCache.Docs.Tests` holds every C# example shown in the docs (`README.md`, `NUGET_README.md`, the package READMEs and `docs/`), so they compile, and tests for the claims the docs make (`ReadmeTests`, `FeatureTests`).
- `samples/`: `MethodCache.Demo` (a guided console tour, run in CI) and `MethodCache.WebDemo` (ASP.NET Core).
- `benchmarks/MethodCache.Benchmarks`: Performance benchmarks.

## Build, Test, Run
- Restore: `dotnet restore`
- Build: `dotnet build MethodCache.sln -c Release`
- Unit tests: `dotnet test MethodCache.sln -c Release`
- Coverage (to `test-results/`): `dotnet test --collect:"XPlat Code Coverage" --results-directory test-results`
- Integration tests (Redis, requires Docker): `dotnet test tests/MethodCache.Redis.IntegrationTests`
- Demo: `dotnet run --project samples/MethodCache.Demo -c Release`

## Coding Style & Naming
- Language: C# (SDK pinned via `global.json`), 4-space indent.
- Naming: PascalCase for types/methods; camelCase for locals/params; `_camelCase` for private fields; async methods end with `Async`.
- Patterns: Prefer DI, immutability where practical, early returns; avoid sync-over-async.
- Formatting/linting: Use built-in analyzers plus project analyzers; run `dotnet format` before PRs.

## Testing Guidelines
- Framework: xUnit. Place tests in `*.Tests` projects; files end with `*Tests.cs`.
- Style: Arrange/Act/Assert, deterministic tests; name methods like `Method_State_Expected`.
- Coverage: Aim for meaningful coverage on new/changed code; include negative and boundary cases.
- Integration: See `tests/MethodCache.Redis.IntegrationTests/README.md`; ensure Docker is running.

## Commits & Pull Requests
- Commits: Imperative present tense (e.g., "Refactor X", "Add Y"); small, focused; include rationale in body when helpful.
- PRs: Clear description, linked issues, testing notes, and any breaking changes. Include docs updates (README or samples) when behavior changes. Add screenshots only if UI/dev tooling is affected.

## Security & Configuration
- Do not log sensitive data or include secrets in cache keys. Use environment variables or user-secrets for Redis connection strings.
- Prefer dependency injection over statics; validate options (`IOptions<T>`) and fail fast on misconfiguration.

## Rules that are easy to miss
- Public API is locked by PublicApiAnalyzers. Any added, removed or changed public member must be recorded in that project's `PublicAPI.Unshipped.txt`, or the build fails.
- Every public type and member in `src/` needs an XML doc comment (`CS1591` is an error). Write what it does for a user, and use `<inheritdoc />` on interface implementations and overrides.
- Nothing is on nuget.org yet, so an API that is replaced is deleted outright. Do not add `[Obsolete]` shims.
- Library code must use `ConfigureAwait(false)` on every await (`CA2007` is an error in `src/`).
- New or changed diagnostics need a row in `docs/user-guide/DIAGNOSTICS.md` and an entry in `src/MethodCache.Analyzers/AnalyzerReleases.Unshipped.md` when they come from the analyzers.
- C# examples in Markdown are not written by hand. They live in `tests/MethodCache.Docs.Tests` between `// begin-snippet: name` and `// end-snippet`, a `.md` file references them with a line `snippet: name`, and `dotnet tool restore && dotnet mdsnippets` copies them in (CI fails when the docs are out of date). Edit the `.cs` file, never the code between `<!-- snippet: -->` and `<!-- endSnippet -->`.
- `llms.txt` and `llms-full.txt` are generated from the pages `docs/README.md` links: after changing docs, run `python3 .github/scripts/generate-llms.py` (CI fails when they are out of date). Keep `docs/ai/methodcache.md` in step with the rules it states.
- Behavior changes need the user docs updated in the same pull request: `README.md`, the feature page under `docs/features/` (indexed in `docs/README.md`), and the package's NuGet README. For `MethodCache` (`src/MethodCache.Core`) that is `NUGET_README.md` at the repository root; the add-on packages use their own `src/*/README.md`.
- Test projects target net8.0, net9.0 and net10.0 (set in `tests/Directory.Build.props`; the smoke tests and `MethodCache.Docs.Tests` keep one framework), so `dotnet test` runs every test three times. Run one framework locally with `-f net10.0`. CI also runs the unit tests on Windows and under the tr-TR and sv-SE cultures, and requires the Docker-backed Redis and SQL Server tests (`METHODCACHE_REQUIRE_INFRA_TESTS=true`) instead of skipping them.
- CI also runs the demo, a Native AOT publish of `tests/MethodCache.AotSmokeTest`, and the package smoke test (including with the .NET 8 SDK). Run the ones your change can affect before pushing.

Tip: When adding a new module, follow the `MethodCache.*` naming, add it to `MethodCache.sln`, and provide minimal tests and a README or example usage.
