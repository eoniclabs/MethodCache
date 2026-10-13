# MethodCache documentation

Start with [Getting started](getting-started.md). Each page below covers one feature, and every C# example on these
pages is compiled and tested in CI.

## Caching methods

- [Getting started](getting-started.md): put `[Cache]` on an interface method and register it.
- [Generated decorators](features/source-generator.md): supported signatures and forwarded interface members.
- [Cache keys](features/keys.md): how keys are built from the arguments, and how to control them.
- [Expiration](features/expiration.md): duration, sliding expiration, refresh-ahead and versions.
- [Invalidation](features/invalidation.md): tags with argument templates, keys and patterns.
- [Fail-safe](features/fail-safe.md): serve the last good value when a dependency fails or is slow.
- [Conditional caching](features/conditional-caching.md): store only the results you want to keep.
- [Configuration](features/configuration.md): defaults, groups, `appsettings.json` with live reload, runtime overrides.

## Beyond your own interfaces

- [Manual caching](features/manual-caching.md): `GetOrCreateAsync` with a key you choose.
- [Third-party code](features/third-party.md): cache a client from a NuGet package.
- [HTTP responses](features/http-caching.md): `HttpClient` caching with `Cache-Control` and `ETag`.

## Running in production

- [Distributed cache](features/distributed-cache.md): Redis, SQL Server, or your own `IDistributedCache` with atomic invalidation stamps.
- [Monitoring](features/monitoring.md): statistics, admin endpoints, health check and OpenTelemetry.
- [Memory cache](features/memory-cache.md): size limits, null values and jitter.
- [Native AOT](features/native-aot.md): what to register when you publish with Native AOT.
- [Testing](features/testing.md): test with caching on, or turn it off.
- [Security](security.md): what each package protects against, and what it leaves to you.

## For coding agents

- [Agent instructions](ai/methodcache.md): rules to copy into Copilot, Cursor or Claude Code instructions.
- [llms.txt](../llms.txt) and [llms-full.txt](../llms-full.txt): these docs for language models, as an index and in one file.

## When something is wrong

- [Troubleshooting](troubleshooting.md): a method that isn't cached, stale data, keys you didn't expect.
- [Build diagnostics](user-guide/DIAGNOSTICS.md): every `MC` and `MCG` warning and error.
- [Migrating from IMemoryCache](migration-from-imemorycache.md): replace hand-written cache-aside code.

## Packages

[Redis](../src/MethodCache.Redis/README.md), [SQL Server](../src/MethodCache.SqlServer/README.md),
[ASP.NET Core](../src/MethodCache.AspNetCore/README.md), [OpenTelemetry](../src/MethodCache.OpenTelemetry/README.md),
[HTTP caching](../src/MethodCache.HttpCaching/README.md). [Benchmarks](benchmarks.md) has the measured performance.

- [Release compatibility notes](developer/RELEASE_NOTES.md): cache key and distributed invalidation formats.
