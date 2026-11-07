<p align="center">
  <img src="assets/branding/icon.svg" alt="MethodCache logo: a lightning bolt between curly braces" width="128" height="128">
</p>

<h1 align="center">MethodCache</h1>

<p align="center">
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10-FFB020?labelColor=1E1B3A" alt=".NET 10"></a>
  <a href="docs/features/native-aot.md"><img src="https://img.shields.io/badge/Native%20AOT-ready-FFB020?labelColor=1E1B3A" alt="Native AOT ready"></a>
  <a href="#performance"><img src="https://img.shields.io/badge/cache%20hit-0%20B%20allocated-FFB020?labelColor=1E1B3A" alt="A cache hit allocates 0 B"></a>
  <a href="https://www.nuget.org/packages/MethodCache"><img src="https://img.shields.io/nuget/vpre/MethodCache?label=NuGet&amp;color=F5F3FF&amp;labelColor=1E1B3A" alt="MethodCache on NuGet"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-F5F3FF?labelColor=1E1B3A" alt="MIT license"></a>
</p>

**Cache any .NET method with one attribute.** MethodCache's source generator writes the caching code for you at
compile time: no reflection, no hand-written keys, no `TryGetValue` boilerplate, and it works with Native AOT.

<!-- snippet: readme-quickstart-interface -->
```cs
using MethodCache;

public interface IUserService
{
    [Cache(Duration = "10m", Tags = ["user:{id}"])]
    Task<User> GetUserAsync(int id);

    [CacheInvalidate(Tags = ["user:{id}"])]
    Task RenameAsync(int id, string name);
}
```
<!-- endSnippet -->

<!-- snippet: readme-quickstart-registration -->
```cs
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddMethodCache();
```
<!-- endSnippet -->

That's the whole setup. `GetUserAsync(42)` runs once and then comes from memory until it expires or
`RenameAsync(42, ...)` invalidates user 42, and only user 42.

## Why MethodCache

- **One attribute, zero boilerplate.** Keys are built from the method and its arguments (values of different types never share a key, nested ones included); your service stays plain code.
- **Fast.** A cache hit allocates nothing and on x64 is about 10 % faster than hand-written `IMemoryCache` code, faster than HybridCache and 8 to 9 times faster than FusionCache ([numbers](#performance)).
- **Compile-time, not runtime.** A Roslyn source generator writes the decorators, analyzers catch mistakes in the editor, and Native AOT is tested in CI.
- **Tags that understand your arguments.** `"user:{id}"` becomes `user:42`, so an update invalidates exactly what it changed.
- **Tune it without redeploying.** Durations and tags come from attributes, a global default, `appsettings.json` (reloaded live) or runtime overrides.
- **Third-party clients too.** One line caches chosen methods of an SDK or API client's own interface, no wrapper needed ([how](#caching-code-you-dont-own)).
- **HTTP responses as well.** `AddHttpCaching()` on an `HttpClient` honors `Cache-Control` and revalidates with ETags ([how](#http-responses)).
- **One line to go distributed.** `AddMethodCache(mc => mc.UseRedis(...))` shares results between instances and keeps every instance's memory cache in sync.
- **Built for production.** Stampede protection, fail-safe, a W-TinyLFU memory cache that stays within its size limit under load ([how strict](docs/features/memory-cache.md#how-strict-the-bound-is)), admin endpoints, a health check and OpenTelemetry tracing and metrics.

## See it run

```bash
git clone https://github.com/eoniclabs/MethodCache
cd MethodCache
dotnet run -c Release --project samples/MethodCache.Demo
```

A tour with eleven chapters and checked claims (CI runs it too). The first chapters measure real calls; expiration,
refresh-ahead and fail-safe use an explicitly advanced demo clock:

```text
  1. One attribute
    First call (runs the method)        425.2 ms
    Second call (from the cache)          0.9 ms
    ✓ WeatherService ran once for 1,100,002 calls

  2. Stampede protection
    ✓ WeatherService ran once
    ✓ All 100 callers got the same result

  3. Tags that understand your arguments
    Load users 1 and 2, twice each    2 database queries
    Rename user 1, load both again    1 new database query
    ✓ User 2 still came from the cache: Alan Turing
  ...
  All 11 chapters behaved as described.
```

See the [console guide](samples/MethodCache.Demo/) for all eleven chapters. For ASP.NET Core with admin endpoints,
OpenTelemetry and two instances sharing Redis, see the [web demo](samples/MethodCache.WebDemo/).

## Before and after

<table>
<tr><th>IMemoryCache by hand</th><th>MethodCache</th></tr>
<tr><td>

<!-- snippet: readme-before -->
```cs
public async Task<User> GetUserAsync(int id)
{
    var key = $"user:{id}";
    if (_cache.TryGetValue(key, out User? user))
        return user!;

    // Two callers can both miss and both hit the database
    user = await _db.LoadUserAsync(id);
    _cache.Set(key, user, TimeSpan.FromMinutes(10));
    return user;
}

public async Task RenameAsync(int id, string name)
{
    await _db.RenameAsync(id, name);
    _cache.Remove($"user:{id}");
    // ...and every other key that held this user
}
```
<!-- endSnippet -->

</td><td>

<!-- snippet: readme-after -->
```cs
[Cache(Duration = "10m", Tags = ["user:{id}"])]
Task<User> GetUserAsync(int id);

[CacheInvalidate(Tags = ["user:{id}"])]
Task RenameAsync(int id, string name);
```
<!-- endSnippet -->

The implementation only talks to the database.
Concurrent misses run it once, and the tag reaches
every cached method tagged with that user.

</td></tr>
</table>

## Getting started

MethodCache 1.0 is a release candidate on nuget.org, so add `--prerelease` until 1.0.0 is out (the same goes for
the add-on packages). Use the newest release candidate: each one replaces the one before it, and the
[changelog](CHANGELOG.md) says what to change when you upgrade.

```bash
dotnet add package MethodCache --prerelease
```

1. Put `[Cache]` on interface methods that return data (sync, `Task<T>` or `ValueTask<T>`), and
   `[CacheInvalidate]` on the methods that change it.
2. Register your services as usual, then call `builder.Services.AddMethodCache()` after them. It wraps every
   registered interface that has cached methods in its generated decorator and keeps the lifetime you chose.
3. Build. Analyzers report mistakes as `MC` diagnostics in the editor.

**Why an interface?** The generator writes a class that implements your interface, checks the cache and calls your
implementation on a miss. Dependency injection hands out that class in place of yours, so code that already uses the
interface doesn't change. A class without one needs an interface, and the constructors, registrations and tests that
name the class switch to it ([how](docs/getting-started.md#a-class-without-an-interface)). You can also use
[manual caching](docs/features/manual-caching.md) with `ICacheManager`, or, for a client from a NuGet package,
[cache its interface](#caching-code-you-dont-own).

**When the data changes somewhere else** (a repository, a message handler, another service), invalidate after the
write with `ICacheManager` and a tag from `IMethodCacheKeys.Tag`, so it's formatted exactly as the cached method's tag
([how](docs/features/invalidation.md#when-the-write-happens-somewhere-else)).

Details: [Getting started](docs/getting-started.md), and [How do I…?](docs/how-to.md) for common questions.

## Features

### Invalidation

Invalidate by tag with `[CacheInvalidate]`, or from code by tag, by key or for a whole method:

<!-- snippet: readme-invalidation -->
```cs
await cache.InvalidateTagAsync(keys.Tag<IUserService>(users => users.GetUserAsync(42), "user:{id}"));
await cache.InvalidateKeyAsync(keys.For<IUserService>(users => users.GetUserAsync(42)));
await cache.InvalidateTagAsync("tenant:7");   // add this tag to all tenant entries
```
<!-- endSnippet -->

`IMethodCacheKeys.Tag` and `IMethodCacheKeys.For` return a call's tag and key, formatted exactly as the cached method
formats them, so you never write one by hand. A hand-built tag misses when it formats a value differently (a `Guid`
is written without hyphens, an enum as its number, `:` is escaped); analyzer MC0024 warns about those.
`ICacheAdministrator.InvalidateMethodAsync` removes every entry of one method. `[CacheInvalidate]` runs after the
method succeeds, never when it throws.

### Configuration without redeploying

Each setting comes from the highest layer that sets it: runtime overrides, then `appsettings.json`, then startup code,
then the attribute. Global and group defaults, also ones set at runtime, fill in what a method leaves unset. Tags
are combined instead: tags from the other layers are added to the attribute's, so `[CacheInvalidate]` keeps working,
unless an override calls `ClearTags()`.

<!-- snippet: readme-configuration -->
```cs
builder.Services.AddMethodCache(config => config
    .Defaults(o => o.WithDuration(TimeSpan.FromMinutes(5)))
    .FromConfiguration(builder.Configuration));   // the "MethodCache" section wins over code
```
<!-- endSnippet -->

```json
{
  "MethodCache": {
    "Methods": {
      "[MyApp]MyApp.IUserService.GetUserAsync": { "Duration": "30m" }
    }
  }
}
```

Each key under `"Methods"` is a method id, as the admin method list shows it, and `"Groups"` sets defaults for a
`[Cache(Group = ...)]` group. Edits to `appsettings.json` apply while the app runs. During an incident, `IRuntimeCacheConfigurator` changes a
policy in code, and the change applies to the next call. See [Configuration](docs/features/configuration.md).

### Distributed cache

<!-- snippet: readme-distributed -->
```cs
builder.Services.AddMethodCache(mc => mc.UseRedis("localhost:6379"));       // or mc.UseSqlServer / mc.UsePostgreSql(connectionString)
```
<!-- endSnippet -->

Results live in memory and in Redis (or SQL Server or PostgreSQL, see
[Distributed cache](docs/features/distributed-cache.md)).
An in-memory miss reads the distributed cache before running your method, and invalidations reach every instance.
Synchronous methods use memory only. Values are JSON by default; a method whose results JSON can't read back
unchanged (private setters, a derived type, `object`) is kept in memory only, with a warning. `mc.UseMessagePack()`
makes values smaller. If Redis goes down, calls keep working from memory: each Redis call is capped at one second, a
circuit breaker skips Redis until it is back, and invalidations are queued and retried instead of throwing.

### Caching code you don't own

To cache a client from a NuGet package, name its interface and the methods to cache:

<!-- snippet: readme-cache-interface -->
```cs
[assembly: CacheInterface(typeof(IFxRatesClient), nameof(IFxRatesClient.GetRateAsync),
    Duration = "1m", Tags = ["fx", "fx:{from}"])]
```
<!-- endSnippet -->

<!-- snippet: readme-cache-interface-registration -->
```cs
builder.Services.AddSingleton<IFxRatesClient, FxRatesClient>();   // the vendor's client, registered as usual
builder.Services.AddMethodCache();
```
<!-- endSnippet -->

`GetRateAsync` now behaves like a `[Cache]` method: keys from the arguments, tags, `appsettings.json` and runtime
overrides. Methods you don't list, such as `PlaceOrderAsync`, always reach the client. For a client without an
interface, wrap the calls in an interface of your own with `[Cache]`, or use the same engine with a key you choose:

<!-- snippet: readme-explicit-key -->
```cs
var rate = await cache.GetOrCreateAsync(
    $"fx:{from}:{to}",
    async ct => await client.GetRateAsync(from, to),
    o => o.WithDuration(TimeSpan.FromMinutes(1)).AddTags("fx"));
```
<!-- endSnippet -->

See [Caching third-party code](docs/features/third-party.md).

### HTTP responses

When the API sends caching headers, let `HttpClient` use them instead of choosing a duration yourself:

<!-- snippet: http-basic -->
```cs
builder.Services.AddMethodCache();
builder.Services.AddHttpClient<IGitHubClient, GitHubClient>()
    .AddHttpCaching();
```
<!-- endSnippet -->

Responses are cached as `Cache-Control` allows and revalidated with `ETag` / `Last-Modified`, so a `304 Not Modified`
reaches your code as the full cached response. A `POST`, `PUT`, `PATCH` or `DELETE` drops the cached responses for
that URL. `AddHttpCaching` requires `AddMethodCache`. With Redis, SQL Server or PostgreSQL configured, responses are
stored there and shared between instances; otherwise they are kept in memory, up to `MaxHttpBytes` (100 MiB by
default). `AddHttpCaching()` turns off automatic cookies on the client's handler; send any cookie the API needs as a
`Cookie` header. See [HTTP response caching](docs/features/http-caching.md) and the [package README](src/MethodCache.HttpCaching/README.md).

### Monitoring

<!-- snippet: readme-monitoring -->
```cs
builder.Services.AddHealthChecks().AddMethodCache();         // MethodCache.AspNetCore
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddMethodCacheInstrumentation())
    .WithMetrics(m => m.AddMethodCacheInstrumentation());

var app = builder.Build();
app.MapMethodCacheAdmin("CacheAdmins");                       // an authorization policy is required
```
<!-- endSnippet -->

The tracing and metrics extensions live in the `OpenTelemetry.Trace` and `OpenTelemetry.Metrics` namespaces, like
every OpenTelemetry instrumentation. The admin endpoints show hits, misses, live entries and every method's effective
policy, and can invalidate by tag.
The same operations are available in code through `ICacheAdministrator`.

### Native AOT

Registration uses no reflection, and neither do keys built from simple values, records, `ICacheKeyProvider` types and
collections of them ([Native AOT](docs/features/native-aot.md#keys) lists the rest). Register the JSON serializer with
your `JsonSerializerContext` with `MethodCacheBuilder.UseJson(context)`:

<!-- snippet: readme-native-aot -->
```cs
builder.Services.AddMethodCache(cache => cache.UseJson(AppJsonContext.Default));
```
<!-- endSnippet -->

## Performance

Warm in-memory hits on an AMD Ryzen 7 5800X (x64, Windows 11), .NET 10.0.13, 2026-10-18. Each library
uses its native API, default options and the same explicitly immutable payload. Key construction is
included. The ratio is against `IMemoryCache` by hand with the same id type (lower is faster):

| Approach | `int` id | `Guid` id | Allocated per hit (`int` / `Guid`) |
|---|---:|---:|---:|
| IMemoryCache by hand | 54.2 ns (1.00x) | 72.1 ns (1.00x) | 112 B / 176 B |
| **MethodCache `[Cache]`** | **49.2 ns (0.91x)** | **62.0 ns (0.86x)** | **0 B / 0 B** |
| HybridCache | 63.5 ns (1.17x) | 77.2 ns (1.07x) | 104 B / 168 B |
| FusionCache | 491.2 ns (9.06x) | 527.6 ns (7.32x) | 344 B / 416 B |

Three independent processes per case, ten warmups and fifteen measured iterations per process,
with 4,194,304 calls per iteration and workstation GC. Times and ratios depend on the processor.
See [the results and methodology](docs/benchmarks.md) for versions, uncertainty and the limits of this
hit-only comparison.

## How it compares

| | MethodCache | HybridCache | FusionCache |
|---|:---:|:---:|:---:|
| Cache a method with an attribute | ✅ | ❌ | ❌ |
| Cache a third-party client's interface without a wrapper | ✅ | ❌ | ❌ |
| Keys built from the arguments | ✅ | ❌ you write keys | ❌ you write keys |
| Stampede protection | ✅ | ✅ | ✅ |
| Fail-safe and soft timeouts | ✅ | ❌ | ✅ |
| Memory + distributed cache | ✅ | ✅ | ✅ |
| Other instances drop their memory copy | ✅ Redis (pub/sub), PostgreSQL (NOTIFY), SQL Server (polling, ~1 s) | ❌ | ✅ |
| Tag invalidation | ✅ with argument templates | ✅ | ✅ |
| Change a method's policy from appsettings.json or at runtime | ✅ | ❌ | ❌ |
| Admin endpoints and health check | ✅ | ❌ | ❌ |

HybridCache ships with .NET and is the natural choice when you want a plain `GetOrCreateAsync` API and nothing more.
FusionCache is more mature and has more resilience features, such as fail-safe in the distributed cache and
auto-recovery. MethodCache is for when you want caching to be a line of metadata on the method rather than code inside it.

## Packages

All packages target .NET 10. Install the add-ons at the same version as `MethodCache`.

| Package | What it adds |
|---|---|
| `MethodCache` | Core, source generator and analyzers. Start here |
| [`MethodCache.Redis`](src/MethodCache.Redis/README.md) | Redis as the distributed cache, with a pub/sub backplane |
| [`MethodCache.SqlServer`](src/MethodCache.SqlServer/README.md) | SQL Server as the distributed cache, with a polling backplane |
| [`MethodCache.PostgreSql`](src/MethodCache.PostgreSql/README.md) | PostgreSQL as the distributed cache, with a `LISTEN`/`NOTIFY` backplane |
| [`MethodCache.AspNetCore`](src/MethodCache.AspNetCore/README.md) | Admin endpoints and a health check |
| [`MethodCache.OpenTelemetry`](src/MethodCache.OpenTelemetry/README.md) | Tracing and metrics |
| [`MethodCache.Serialization.MessagePack`](src/MethodCache.Serialization.MessagePack/README.md) | MessagePack instead of JSON in the distributed cache |
| [`MethodCache.HttpCaching`](src/MethodCache.HttpCaching/README.md) | HTTP caching for `HttpClient`: Cache-Control, ETag revalidation, Vary, optionally shared through the distributed cache |
| [`MethodCache.EntityFrameworkCore`](src/MethodCache.EntityFrameworkCore/README.md) | Invalidation after Entity Framework Core commits: tags per entity type, and a warning for writes it can't see |
| [`MethodCache.Testing`](src/MethodCache.Testing/README.md) | Test helpers: a fake clock, a recorder of loads and invalidations, and a check that every tag has an invalidation |

## Documentation

The [documentation](docs/README.md) has a page per feature, and every C# example in it is compiled and tested in CI:

- [Getting started](docs/getting-started.md) and [How do I…?](docs/how-to.md), short answers to common questions
- Caching methods: [generated decorators](docs/features/source-generator.md), [keys](docs/features/keys.md), [expiration](docs/features/expiration.md),
  [invalidation](docs/features/invalidation.md), [Entity Framework Core](docs/features/entity-framework-core.md), [fail-safe](docs/features/fail-safe.md),
  [conditional caching](docs/features/conditional-caching.md), [configuration](docs/features/configuration.md)
- [Manual caching](docs/features/manual-caching.md) and [third-party code](docs/features/third-party.md)
- Production: [distributed cache](docs/features/distributed-cache.md), [monitoring](docs/features/monitoring.md),
  [memory cache](docs/features/memory-cache.md), [Native AOT](docs/features/native-aot.md), [testing](docs/features/testing.md),
  [security](docs/security.md)
- [Troubleshooting](docs/troubleshooting.md), [build diagnostics](docs/user-guide/DIAGNOSTICS.md) and
  [migrating from IMemoryCache](docs/migration-from-imemorycache.md)
- Reference, with the exact rules: [registration](docs/reference/registration.md),
  [distributed cache](docs/reference/distributed.md), [HTTP caching](docs/reference/http-caching.md)
- Package guides: [Redis](src/MethodCache.Redis/README.md), [SQL Server](src/MethodCache.SqlServer/README.md), [PostgreSQL](src/MethodCache.PostgreSql/README.md), [ASP.NET Core](src/MethodCache.AspNetCore/README.md), [OpenTelemetry](src/MethodCache.OpenTelemetry/README.md), [HTTP caching](src/MethodCache.HttpCaching/README.md), [MessagePack](src/MethodCache.Serialization.MessagePack/README.md), [Entity Framework Core](src/MethodCache.EntityFrameworkCore/README.md), [Testing](src/MethodCache.Testing/README.md)
- [Console tour, including HttpClient caching](samples/MethodCache.Demo/) and [web demo](samples/MethodCache.WebDemo/)

## Using a coding agent

The `MethodCache` package carries this documentation in its `docs` folder, so an agent can read the docs for the
version you installed without network access. [llms.txt](llms.txt) and [llms-full.txt](llms-full.txt) have the same
docs online, and `context7.json` lets Context7 index them. Give your agent this prompt:

```text
Add caching with MethodCache (NuGet package MethodCache) to this project.

Before you write code, read the MethodCache docs. The package ships them: look in
~/.nuget/packages/methodcache/<installed version>/docs/ (on Windows %USERPROFILE%\.nuget\packages\...),
starting with ai/methodcache.md and how-to.md. If the package isn't installed yet, read
https://raw.githubusercontent.com/eoniclabs/MethodCache/main/llms-full.txt instead.

Then:
1. Find reads that are slow or repeated and whose results can be a little old. Make sure each is behind
   an interface that is registered in dependency injection; add an interface where there is none.
2. Put [Cache] with a Duration and Tags on those interface methods, and [CacheInvalidate] with the same
   tags on the methods of the same interfaces that change the data.
3. Where the data changes somewhere else (another service, a repository, a message handler), invalidate
   after the write with ICacheManager and a tag from IMethodCacheKeys.Tag or a key from
   IMethodCacheKeys.For. Never build tag or key strings by hand.
4. Register the services as before, and call AddMethodCache() after them.
5. Build, and fix every MC diagnostic.
6. Add tests that show a cached read and its invalidation.

Report which methods you cached, for how long, and how each one is invalidated. If the MethodCache docs were
wrong or missing, or you needed a workaround, draft feedback as ai/methodcache.md describes under Feedback, without
our code, names, secrets or other sensitive information, and show it to me before you send it.
```
