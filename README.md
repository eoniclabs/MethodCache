# MethodCache

[![.NET](https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![NuGet](https://img.shields.io/badge/NuGet-not%20yet%20published-lightgrey)](https://www.nuget.org/packages?q=MethodCache)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**Cache any .NET method with one attribute.** MethodCache's source generator writes the caching code for you at
compile time: no reflection, no hand-written keys, no `TryGetValue` boilerplate, and it works with Native AOT.

<!-- snippet: readme-quickstart-interface -->
```cs
using MethodCache;

public interface IUserService
{
    [Cache(Duration = "00:10:00", Tags = ["user:{id}"])]
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
- **Fast.** A cache hit allocates nothing, is as fast as hand-written `IMemoryCache` code, and costs 4 to 6 times less than HybridCache or FusionCache ([numbers](#performance)).
- **Compile-time, not runtime.** A Roslyn source generator writes the decorators, analyzers catch mistakes in the editor, and Native AOT is tested in CI.
- **Tags that understand your arguments.** `"user:{id}"` becomes `user:42`, so an update invalidates exactly what it changed.
- **Tune it without redeploying.** Durations and tags come from attributes, a global default, `appsettings.json` (reloaded live) or runtime overrides.
- **Third-party clients too.** One line caches chosen methods of an SDK or API client's own interface, no wrapper needed ([how](#caching-code-you-dont-own)).
- **HTTP responses as well.** `AddHttpCaching()` on an `HttpClient` honors `Cache-Control` and revalidates with ETags ([how](#http-responses)).
- **One line to go distributed.** `AddMethodCacheRedis(...)` shares results between instances and keeps every instance's memory cache in sync.
- **Built for production.** Stampede protection, fail-safe, a W-TinyLFU memory cache that stays within its size limit under load ([how strict](docs/features/memory-cache.md#how-strict-the-bound-is)), admin endpoints, a health check and OpenTelemetry tracing and metrics.

## See it run

```bash
git clone https://github.com/eoniclabs/MethodCache
cd MethodCache
dotnet run -c Release --project samples/MethodCache.Demo
```

A six-chapter tour where every number is measured live and every claim is checked (CI runs it too):

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
  All 6 chapters behaved as described.
```

For ASP.NET Core with admin endpoints, OpenTelemetry and two instances sharing Redis, see the
[web demo](samples/MethodCache.WebDemo/).

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
[Cache(Duration = "00:10:00", Tags = ["user:{id}"])]
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

MethodCache is not on nuget.org yet. Until it is, reference the projects from this repository (the
[demo's project file](samples/MethodCache.Demo/MethodCache.Demo.csproj) shows how). Once published:

```bash
dotnet add package MethodCache
```

1. Put `[Cache]` on interface methods that return data (sync, `Task<T>` or `ValueTask<T>`).
2. Register your services as usual, then call `builder.Services.AddMethodCache()`. It wraps every registered
   interface that has cached methods in its generated decorator and keeps the lifetime you chose.

A service registered after `AddMethodCache()` is not cached. If you don't register an implementation at all,
`AddMethodCache()` registers it as a singleton when it lives in the same project as the interface.
Generic interfaces such as `IRepository<T>` are cached for each closed type your project implements
(`UserRepository : IRepository<User>`); an open generic registration (`typeof(IRepository<>)`) is not.
Key generators preserve exact UTF-16 string identity, including custom key-provider parts and nested values.
C# keyword and reserved-looking parameter names, generic constraints, nested generic interfaces and overload names are supported.
Automatic decoration preserves normal DI disposal ownership, including application-supplied instances.

## Features

Non-public default interface helpers are supported. Interfaces with non-public abstract members report `MCG013`.

### Invalidation

Invalidate by tag with `[CacheInvalidate]`, or from code by tag, key or pattern:

<!-- snippet: readme-invalidation -->
```cs
await cache.InvalidateByTagsAsync("user:42");
await cache.InvalidateByKeysAsync(MethodCacheKeys.For(typeof(IUserService), nameof(IUserService.GetUserAsync), MethodCacheKeys.Part(42)));
await cache.InvalidateByTagPatternAsync("tenant:7:*");   // in-memory only
```
<!-- endSnippet -->

Generated keys look like `MyApp.IUserService.GetUserAsync:42`, and `MethodCacheKeys` builds the same keys, so you
never guess the format.

### Configuration without redeploying

Each setting comes from the highest layer that sets it: runtime overrides, then `appsettings.json`, then startup code,
then the attribute. Global and group defaults fill in what a method leaves unset.

<!-- snippet: readme-configuration -->
```cs
builder.Services.AddMethodCache(config =>
    config.DefaultPolicy(o => o.WithDuration(TimeSpan.FromMinutes(5))));
builder.Services.AddMethodCacheFromConfiguration(builder.Configuration);
```
<!-- endSnippet -->

```json
{
  "MethodCache": {
    "Services": {
      "MyApp.IUserService.GetUserAsync": { "Duration": "00:30:00" }
    }
  }
}
```

Edits to `appsettings.json` apply while the app runs. During an incident, `IRuntimeCacheConfigurator` changes a
policy in code, and the change applies to the next call. See [Configuration](docs/features/configuration.md).

### Distributed cache

L2 entries expire before their tag/clear markers can be forgotten. Their maximum shared lifetime is the effective `TagRetention - ClockSkewTolerance` measured from the source read, so slow loads reduce the remaining L2 lifetime; a load that exceeds this window stays in its loading node only.

<!-- snippet: readme-distributed -->
```cs
builder.Services.AddMethodCache();
builder.Services.AddMethodCacheRedis("localhost:6379");       // or AddMethodCacheSqlServer(connectionString)
```
<!-- endSnippet -->

Results live in memory and in Redis (or SQL Server, or your own cache via `AddMethodCacheDistributedCache()`, which
must implement `IAtomicInvalidationStore` as well as `IDistributedCache`; if it doesn't, the host fails at startup).
An in-memory miss reads the distributed cache before running your method, and invalidations reach every instance.
Values are JSON by default; `AddMethodCacheMessagePack()` makes them smaller.
If Redis goes down, calls keep working from memory: each Redis call is capped at one second, a circuit breaker skips
Redis until it is back, and invalidations are queued and retried instead of throwing. A value you set directly
during the outage stays in that instance's memory, and the value it replaced is removed everywhere once Redis is back.
SQL Server tables created by an earlier build need the schema upgrade in the [SQL Server guide](src/MethodCache.SqlServer/README.md).

### Caching code you don't own

To cache a client from a NuGet package, name its interface and the methods to cache:

<!-- snippet: readme-cache-interface -->
```cs
[assembly: CacheInterface(typeof(IFxRatesClient), nameof(IFxRatesClient.GetRateAsync),
    Duration = "00:01:00", Tags = ["fx", "fx:{from}"])]
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
    async (ctx, ct) => await client.GetRateAsync(from, to),
    o => o.WithDuration(TimeSpan.FromMinutes(1)).WithTags("fx"));
```
<!-- endSnippet -->

`CacheEntryOptions.Builder.Build()` snapshots tags and callbacks; reusing the builder does not change previously built options. Fluent key versions cannot alias unversioned user keys.

See [Caching third-party code](docs/features/third-party.md).

### HTTP responses

Automatic transport cookies bypass caching because the cookie jar can change below the cache handler. For cached clients, configure `SocketsHttpHandler` or `HttpClientHandler` with `UseCookies = false` and send any required `Cookie` header explicitly before the cache handler. The default private cache isolates those explicit cookie headers.

A failed response-body read fails the request rather than returning a partially consumed body. Buffered and streamed responses release their original content resources on completion, cancellation or disposal.

`only-if-cached` never contacts the origin, including range requests, caller validators, `no-store`, unsupported methods and background revalidation. An unavailable representation returns 504.

HTTP cache keys preserve individual header-value boundaries, including `Cookie` and custom tenant headers.

An explicit `Expires` at or before the response date is stale immediately; heuristic freshness and `DefaultMaxAge` do not extend it.

When the API sends caching headers, let `HttpClient` use them instead of choosing a duration yourself:

<!-- snippet: http-basic -->
```cs
builder.Services.AddHttpClient<IGitHubClient, GitHubClient>()
    .AddHttpCaching();
```
<!-- endSnippet -->

Responses are cached as `Cache-Control` allows and revalidated with `ETag` / `Last-Modified`, so a `304 Not Modified`
reaches your code as the full cached response. A `POST`, `PUT`, `PATCH` or `DELETE` drops the cached responses for
that URL. With `UseDistributedCache`, all instances share the responses through MethodCache's Redis or SQL Server
cache (or the app's `IDistributedCache` when neither is registered).
See [HTTP response caching](docs/features/http-caching.md) and the [package README](src/MethodCache.HttpCaching/README.md).

### Monitoring

Tracing counts cached `null` values as hits, including nullable value types.

<!-- snippet: readme-monitoring -->
```cs
builder.Services.AddHealthChecks().AddMethodCache();         // MethodCache.AspNetCore
builder.Services.AddMethodCacheOpenTelemetry();               // MethodCache.OpenTelemetry
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

Registration and keys use no reflection. Under Native AOT, register the JSON serializer with your
`JsonSerializerContext` (`ISerializer` and `JsonCacheSerializer` are in `MethodCache.Extensibility`):

<!-- snippet: readme-native-aot -->
```cs
builder.Services.AddSingleton<ISerializer>(new JsonCacheSerializer(AppJsonContext.Default.Options));
```
<!-- endSnippet -->

## Performance

A cache hit on `Task<User> GetUserAsync(int id)`, each library on its default settings:

| Approach | Mean | vs. `IMemoryCache` by hand | Allocated per hit |
|---|---:|---:|---:|
| `IMemoryCache` by hand | 92 ns | 1.00x | 112 B |
| **MethodCache `[Cache]`** | **84 ns** | **0.92x** | **0 B** |
| HybridCache | 497 ns | 5.4x | 272 B |
| FusionCache | 348 ns | 3.8x | 344 B |

The generated decorator writes the key into a stack buffer and returns the cached `Task` itself, so a hit allocates
nothing and is at least as fast as hand-written code (on .NET 9 and later; .NET 8 builds the key as a string).
Measured with BenchmarkDotNet on .NET 10 on a shared cloud VM (Intel Xeon 2.1 GHz, 4 cores). Times and ratios
are for that setup and will differ on other hardware and runtimes; the allocations don't.
[docs/benchmarks.md](docs/benchmarks.md) has the full results. To measure on your own machine:

```bash
dotnet run -c Release --project benchmarks/MethodCache.Benchmarks -- readme --filter '*Hit'
```

## How it compares

| | MethodCache | HybridCache | FusionCache |
|---|:---:|:---:|:---:|
| Cache a method with an attribute | ✅ | ❌ | ❌ |
| Cache a third-party client's interface without a wrapper | ✅ | ❌ | ❌ |
| Keys built from the arguments | ✅ | ❌ you write keys | ❌ you write keys |
| Stampede protection | ✅ | ✅ | ✅ |
| Fail-safe and soft timeouts | ✅ | ❌ | ✅ |
| Memory + distributed cache | ✅ | ✅ | ✅ |
| Other instances drop their memory copy at once | ✅ Redis, SQL Server | ❌ | ✅ |
| Tag invalidation | ✅ with argument templates | ✅ | ✅ |
| Change a method's policy from appsettings.json or at runtime | ✅ | ❌ | ❌ |
| Admin endpoints and health check | ✅ | ❌ | ❌ |

HybridCache ships with .NET and is the natural choice when you want a plain `GetOrCreateAsync` API and nothing more.
FusionCache is more mature and has more resilience features, such as fail-safe in the distributed cache and
auto-recovery. MethodCache is for when you want caching to be a line of metadata on the method rather than code inside it.

## Packages

Supported frameworks: .NET 8, 9 and 10.

| Package | What it adds | Status |
|---|---|---|
| `MethodCache` | Core, source generator and analyzers. Start here | Stable |
| [`MethodCache.Redis`](src/MethodCache.Redis/README.md) | Redis as the distributed cache, with a pub/sub backplane | Beta |
| [`MethodCache.SqlServer`](src/MethodCache.SqlServer/README.md) | SQL Server as the distributed cache, with a polling backplane | Experimental |
| [`MethodCache.AspNetCore`](src/MethodCache.AspNetCore/README.md) | Admin endpoints and a health check | Experimental |
| [`MethodCache.OpenTelemetry`](src/MethodCache.OpenTelemetry/README.md) | Tracing and metrics | Beta |
| `MethodCache.Serialization.MessagePack` | MessagePack instead of JSON in the distributed cache | Experimental |
| [`MethodCache.HttpCaching`](src/MethodCache.HttpCaching/README.md) | HTTP caching for `HttpClient`: Cache-Control, ETag revalidation, Vary, optionally shared through the distributed cache | Experimental |

## Documentation

The [documentation](docs/README.md) has a page per feature, and every C# example in it is compiled and tested in CI:

- [Getting started](docs/getting-started.md)
- Caching methods: [generated decorators](docs/features/source-generator.md), [keys](docs/features/keys.md), [expiration](docs/features/expiration.md),
  [invalidation](docs/features/invalidation.md), [fail-safe](docs/features/fail-safe.md),
  [conditional caching](docs/features/conditional-caching.md), [configuration](docs/features/configuration.md)
- [Manual caching](docs/features/manual-caching.md) and [third-party code](docs/features/third-party.md)
- Production: [distributed cache](docs/features/distributed-cache.md), [monitoring](docs/features/monitoring.md),
  [memory cache](docs/features/memory-cache.md), [Native AOT](docs/features/native-aot.md), [testing](docs/features/testing.md),
  [security](docs/security.md)
- [Troubleshooting](docs/troubleshooting.md), [build diagnostics](docs/user-guide/DIAGNOSTICS.md) and
  [migrating from IMemoryCache](docs/migration-from-imemorycache.md)
- Package guides: [Redis](src/MethodCache.Redis/README.md), [SQL Server](src/MethodCache.SqlServer/README.md), [ASP.NET Core](src/MethodCache.AspNetCore/README.md), [OpenTelemetry](src/MethodCache.OpenTelemetry/README.md), [HTTP caching](src/MethodCache.HttpCaching/README.md)
- [Demo](samples/MethodCache.Demo/) and [web demo](samples/MethodCache.WebDemo/)
- Using a coding agent? Give it [docs/ai/methodcache.md](docs/ai/methodcache.md). [llms.txt](llms.txt) and [llms-full.txt](llms-full.txt) have the docs for language models, and `context7.json` lets Context7 index them.

## Behavior and compatibility

Invalidation methods preserve synchronous `ref` and `ref readonly` returns. `Task` and `ValueTask` invalidation methods may use `ref`, `out` or `in` parameters: tags are captured after the underlying call returns, and invalidation runs only after its asynchronous operation succeeds.

Tag placeholders prefer the exact C# parameter spelling. A unique case-insensitive match is also accepted; ambiguous placeholders stay literal and report generator warning `MCG016`. This rule applies to both cached entries and invalidation.

Distributed tags and backplane keys/tags preserve exact UTF-16 code units, including lone surrogates. Envelope version 4 and backplane frames use lossless representations; older envelopes are cache misses. Deploy the same MethodCache version across nodes sharing a cache and backplane.

Redis escapes unpaired UTF-16 surrogates and backslashes in physical keys, keeping distinct logical keys separate in every data and invalidation operation. Ordinary keys and their prefixes stay unchanged.

An invalidated in-flight memory load cannot publish over a newer load or explicit write. Publication and invalidation are coordinated per load; application weighers run outside that coordination so they may call the cache safely.

`ExistsAsync` reports an entry only while it is both logically fresh and physically retained. A fail-safe retry window never makes existence extend beyond the physical fail-safe lifetime.

An invalid `Expires` date (including `Expires: 0`) is immediately stale, even when heuristic freshness or a default lifetime is enabled. Explicit `max-age` and `s-maxage` take precedence.

Fail-safe retry throttling is capped at physical expiration. Administration lists, entry details and removal results never report physically expired entries as live.

Tracing uses `ICacheManager.LookupAsync` to distinguish a cached null from a miss in one memory lookup, including disabled fast paths and refresh-ahead windows. Like `TryGetAsync`, it does not load from L2 or start a factory.


Unknown HTTP transports (including opaque wrappers and native platform handlers) bypass caching by default and emit one warning per named client. Set `AllowUnverifiedTransport` only when you have verified that the whole transport sends no automatic cookies or hidden caller identity. Recognized transports with automatic cookies always bypass.

`AddHttpCaching()` disables automatic cookies on the named client’s `SocketsHttpHandler` or `HttpClientHandler`, after all primary-handler configuration. The default registration therefore caches immediately. Send caller cookies explicitly; a handler that manages a hidden cookie jar belongs on a client without HTTP caching. Other named clients are unaffected.

Factory registration helpers dispose owned implementations even when their service interface does not declare disposal. Asynchronous disposal prefers `IAsyncDisposable`, then falls back to `IDisposable`. Automatically discovered DI services retain container ownership.

Sealed default interface helpers retain their interface bodies and are never reimplemented by a decorator.

Backplane invalidations do not cross wire versions in either direction. Deploy the same MethodCache version on all nodes sharing a cache/backplane; a rolling upgrade needs a new cache namespace or a coordinated cache clear. New message type numbers prevent legacy keys from being mistaken for encoded frames. Rejected frames emit one warning per backplane instance without logging their contents.

`TagRetention` must exceed `ClockSkewTolerance`; invalid settings fail startup or direct layer construction. L2 entries live at most `TagRetention - ClockSkewTolerance` from their source read, capped further by their policy duration.

Generated decorators qualify framework and cache infrastructure type names globally, so application types such as `Task`, `List` or `CachedMethod` cannot shadow them.

Oversized HTTP responses remain streamed; disposing them closes both stream segments and the original content owner, even if a custom owner does not close its stream.

Runtime tag templates match against every method parameter, even when the attributes use only a subset. Exact names take precedence; an ambiguous case-insensitive placeholder remains literal. Untagged policies do not format argument values.

Cached interfaces may declare nested helper interfaces and abstract types. These declarations do not trigger MCG013.

An invalidation attribute without tags forwards the operation without issuing an empty tag invalidation, including asynchronous methods with reference parameters.

Versioned fluent entries occupy a reserved namespace. Use `CacheManagerExtensions.GetCacheKey(logicalKey, version)` when directly invalidating or administering one; do not construct its internal control-character frame by hand. `CacheContext.Key` keeps the logical key for callbacks and factories.

HTTP key framing changes all response cache keys, including requests without `Vary`; old entries become cold misses. See the [release compatibility notes](docs/developer/RELEASE_NOTES.md).

`AddHttpCaching()` runs the cache after all additional handlers, so injected caller identity and rewritten requests are visible before lookup. Delegating handlers inside the primary transport, or below a directly constructed cache handler, are unverified and bypass caching unless the entire chain is explicitly trusted.

L2 timestamps have millisecond precision: `TagRetention` and `ClockSkewTolerance` are each truncated to whole milliseconds before validation and lifetime calculation. Their effective difference must be at least one millisecond; settings that collapse to zero fail startup or direct construction.

Unversioned fluent keys that begin with NUL are escaped too. Use `CacheManagerExtensions.GetCacheKey(logicalKey)` for direct invalidation or administration of those entries, just as you use the overload with a version for versioned entries.

Generated runtime type checks use the same generic-parameter aliases as decorator signatures, including unsealed generic argument types whose simple members form inline cache keys.

MessagePack keys preserve derived member data in nested base-class/interface slots and collection elements. Earlier keys for these arguments change; existing entries become cold misses and expire normally.

Manual `TryGetAsync` reads a fresh entry, including a cached null, even when the fast path is disabled or refresh-ahead is due. This lookup does not start a factory or background refresh.

An explicit `SetAsync` with a lifetime shorter than L2 millisecond precision removes the previous distributed value instead of leaving it readable. If the backend is unavailable, the removal follows the normal invalidation retry path.

Overloaded generic methods include generic arity in their policy ids and key prefixes: `Get(int)`, `Get``1(int)` and `Get``2(int)` remain distinct. Generator and runtime configuration use the same ids; earlier keys for overloaded generic methods become cold misses.

Caller cancellation stops the wait during an L2 read or a slow factory, including bulk reads and fail-safe waits. Shared work may finish for other callers; cancellation is not reported as a dependency failure.

Policy tags are owned, read-only snapshots in both `CachePolicy` and `CacheRuntimePolicy`. Mutating the collection supplied by application code cannot change a published policy or bypass its version/change notifications.
