# Repository Guidelines

## Project Structure & Modules
- `MethodCache.Core`: Core APIs, attributes, in-memory cache.
- `MethodCache.SourceGenerator`: Roslyn source generator for decorators.
- `MethodCache.Analyzers`: Compile-time checks and diagnostics.
- `MethodCache.Providers.Redis`: Redis L2 provider (+ compression, tags, locks).
- `MethodCache.HybridCache`: L1/L2 orchestration and policies.
- `MethodCache.ETags`: HTTP ETag support layered on MethodCache.
- `MethodCache.Tests`, `*.Tests`: xUnit unit tests; `*.IntegrationTests`: Docker-backed Redis tests.
- `MethodCache.SampleApp` and `MethodCache.Demo`: runnable examples.

## Build, Test, Run
- Restore: `dotnet restore`
- Build: `dotnet build MethodCache.sln -c Release`
- Unit tests: `dotnet test MethodCache.sln -c Release`
- Coverage (to `test-results/`): `dotnet test --collect:"XPlat Code Coverage" --results-directory test-results`
- Integration tests (Redis, requires Docker): `dotnet test MethodCache.Providers.Redis.IntegrationTests`
- Sample app: `dotnet run --project MethodCache.SampleApp`

## Coding Style & Naming
- Language: C# (SDK pinned via `global.json`), 4-space indent.
- Naming: PascalCase for types/methods; camelCase for locals/params; `_camelCase` for private fields; async methods end with `Async`.
- Patterns: Prefer DI, immutability where practical, early returns; avoid sync-over-async.
- Formatting/linting: Use built-in analyzers plus project analyzers; run `dotnet format` before PRs.

## Testing Guidelines
- Framework: xUnit. Place tests in `*.Tests` projects; files end with `*Tests.cs`.
- Style: Arrange/Act/Assert, deterministic tests; name methods like `Method_State_Expected`.
- Coverage: Aim for meaningful coverage on new/changed code; include negative and boundary cases.
- Integration: See `MethodCache.Providers.Redis.IntegrationTests/README.md`; ensure Docker is running.

## Commits & Pull Requests
- Commits: Imperative present tense (e.g., "Refactor X", "Add Y"); small, focused; include rationale in body when helpful.
- PRs: Clear description, linked issues, testing notes, and any breaking changes. Include docs updates (README or samples) when behavior changes. Add screenshots only if UI/dev tooling is affected.

## Security & Configuration
- Do not log sensitive data or include secrets in cache keys. Use environment variables or user-secrets for Redis connection strings.
- Prefer dependency injection over statics; validate options (`IOptions<T>`) and fail fast on misconfiguration.

Tip: When adding a new module, follow the `MethodCache.*` naming, add it to `MethodCache.sln`, and provide minimal tests and a README or example usage.

