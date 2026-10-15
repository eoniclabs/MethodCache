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

Details: [Getting started](docs/getting-started.md).

Missing services require explicit `AutoRegister(lifetime)`, with exactly one accessible implementation in the
selected generated module. Use `FromAssemblyOf<T>()` to select between modules; ambiguous module/candidate choices
and unsupported cacheable bindings fail explicitly. Startup configuration uses `MethodCacheBuilder`.

SQL Server backplane tables require a non-nullable `binary(32)` `Scope` column. Automatic initialization reports
incompatible schemas without upgrading them; see the [SQL Server guide](src/MethodCache.SqlServer/README.md#required-backplane-schema).

## Features

### Invalidation

Invalidate by tag with `[CacheInvalidate]`, or from code by tag, key or pattern:

<!-- snippet: readme-invalidation -->
```cs
await cache.InvalidateTagAsync("user:42");
await cache.InvalidateKeyAsync(MethodCacheKeys.For(typeof(IUserService), nameof(IUserService.GetUserAsync), MethodCacheKeys.Part(42)));
await cache.InvalidateTagAsync("tenant:7");   // add this tag to all tenant entries
```
<!-- endSnippet -->

Generated keys look like `MyApp.IUserService.GetUserAsync:42` (a record argument as `(alice:2)`, an array or list as
`[3:1:2:3]`), and `MethodCacheKeys` builds the same keys, so you never guess the format.

### Configuration without redeploying

Each setting comes from the highest layer that sets it: runtime overrides, then `appsettings.json`, then startup code,
then the attribute. Global and group defaults, also ones set at runtime, fill in what a method leaves unset. Tags
are combined instead: tags from the other layers are added to the attribute's, so `[CacheInvalidate]` keeps working,
unless an override calls `ClearTags()`.

<!-- snippet: readme-configuration -->
```cs
builder.Services.AddMethodCache(config =>
    config.Defaults(o => o.WithDuration(TimeSpan.FromMinutes(5))));
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

<!-- snippet: readme-distributed -->
```cs
builder.Services.AddMethodCache();
builder.Services.AddMethodCacheRedis("localhost:6379");       // or AddMethodCacheSqlServer(connectionString)
```
<!-- endSnippet -->

Results live in memory and in Redis (or SQL Server, or your own cache, see
[Distributed cache](docs/features/distributed-cache.md)).
An in-memory miss reads the distributed cache before running your method, and invalidations reach every instance.
Values are JSON by default; a method whose results JSON can't read back unchanged (private setters, a derived type,
`object`) is kept in memory only, with a warning. `AddMethodCacheMessagePack()` makes values smaller.
If Redis goes down, calls keep working from memory: each Redis call is capped at one second, a circuit breaker skips
Redis until it is back, and invalidations are queued and retried instead of throwing. A value you set directly
during the outage stays in that instance's memory, and the value it replaced is removed everywhere once Redis is back.

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

See [Caching third-party code](docs/features/third-party.md).

### HTTP responses

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
cache (or the app's `IDistributedCache` when neither is registered). `AddHttpCaching()` turns off automatic cookies
on the client's handler; send any cookie the API needs as an explicit `Cookie` header. Redirects the transport
follows are not stored under the URL you asked for, retries (Polly, standard resilience) get full responses, and a
`GET` with a body goes to the origin.
See [HTTP response caching](docs/features/http-caching.md) and the [package README](src/MethodCache.HttpCaching/README.md).

### Monitoring

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

Registration uses no reflection, and neither do keys built from simple values, records, `ICacheKeyProvider` types and
collections of them ([Native AOT](docs/features/native-aot.md#keys) lists the rest). Register the JSON serializer with
your `JsonSerializerContext` with `MethodCacheBuilder.UseJson(context)`:

<!-- snippet: readme-native-aot -->
```cs
builder.Services.AddMethodCache(cache => cache.UseJson(AppJsonContext.Default));
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
| Other instances drop their memory copy | ✅ Redis (pub/sub), SQL Server (polling, ~1 s) | ❌ | ✅ |
| Tag invalidation | ✅ with argument templates | ✅ | ✅ |
| Change a method's policy from appsettings.json or at runtime | ✅ | ❌ | ❌ |
| Admin endpoints and health check | ✅ | ❌ | ❌ |

HybridCache ships with .NET and is the natural choice when you want a plain `GetOrCreateAsync` API and nothing more.
FusionCache is more mature and has more resilience features, such as fail-safe in the distributed cache and
auto-recovery. MethodCache is for when you want caching to be a line of metadata on the method rather than code inside it.

## Packages

Supported frameworks: .NET 8, 9 and 10.

| Package | What it adds |
|---|---|
| `MethodCache` | Core, source generator and analyzers. Start here |
| [`MethodCache.Redis`](src/MethodCache.Redis/README.md) | Redis as the distributed cache, with a pub/sub backplane |
| [`MethodCache.SqlServer`](src/MethodCache.SqlServer/README.md) | SQL Server as the distributed cache, with a polling backplane |
| [`MethodCache.AspNetCore`](src/MethodCache.AspNetCore/README.md) | Admin endpoints and a health check |
| [`MethodCache.OpenTelemetry`](src/MethodCache.OpenTelemetry/README.md) | Tracing and metrics |
| [`MethodCache.Serialization.MessagePack`](src/MethodCache.Serialization.MessagePack/README.md) | MessagePack instead of JSON in the distributed cache |
| [`MethodCache.HttpCaching`](src/MethodCache.HttpCaching/README.md) | HTTP caching for `HttpClient`: Cache-Control, ETag revalidation, Vary, optionally shared through the distributed cache |

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
- Package guides: [Redis](src/MethodCache.Redis/README.md), [SQL Server](src/MethodCache.SqlServer/README.md), [ASP.NET Core](src/MethodCache.AspNetCore/README.md), [OpenTelemetry](src/MethodCache.OpenTelemetry/README.md), [HTTP caching](src/MethodCache.HttpCaching/README.md), [MessagePack](src/MethodCache.Serialization.MessagePack/README.md)
- [Demo](samples/MethodCache.Demo/) and [web demo](samples/MethodCache.WebDemo/)
- Using a coding agent? Give it [docs/ai/methodcache.md](docs/ai/methodcache.md). [llms.txt](llms.txt) and [llms-full.txt](llms-full.txt) have the docs for language models, and `context7.json` lets Context7 index them.

## Additional behavior

MessagePack keys preserve derived member data in nested base-class/interface slots and collection elements. Earlier keys for these arguments change; existing entries become cold misses and expire normally.

Manual `TryGetAsync` reads a fresh entry, including a cached null, even when the fast path is disabled or refresh-ahead is due. This lookup does not start a factory or background refresh.

Overloaded generic methods include generic arity in their policy ids and key prefixes: `Get(int)`, `Get``1(int)` and `Get``2(int)` remain distinct. Generator and runtime configuration use the same ids; earlier keys for overloaded generic methods become cold misses.

Caller cancellation stops the wait during an L2 read or a slow factory, including bulk reads and fail-safe waits. L2 reads pass cancellation to the backend; shared factory work may finish for other callers. Caller cancellation is not reported as a dependency failure.

Bulk reads guard their source work against key, tag and clear invalidation: an invalidated batch result is returned to its caller but is not published. Collection subclasses keep their own public properties and fields in hashed keys, as well as their elements. Static tags do not format arguments; templates format only the parameters they reference. Redis backplane publications honor caller cancellation, and a cancelled unsubscribe acknowledgement does not fail application shutdown. SQL backplane gaps never hold newer invalidations behind a locked transaction, and HTTP variant schema changes retire their publication epoch once; a failed deletion after a successful rotation does not retire a newer concurrent publication.

Policy tags are owned, read-only snapshots in both `CachePolicy` and `CacheRuntimePolicy`. Mutating the collection supplied by application code cannot change a published policy or bypass its version/change notifications.

HTTP cache freshness includes request/response delay when correcting the upstream Age header, for both initial responses and 304 validation. Age arithmetic saturates instead of overflowing on extreme headers.

Background HTTP revalidation preserves the request HTTP version, version policy and a shallow copy of its request options. Option values keep their original ownership; use values that remain valid for background work.

A keyed `ICacheManager` registration remains independent of the default unkeyed manager. Registering another service key does not suppress `AddMethodCache()` or its default administration services.

Keyed registrations of cached interfaces are decorated under their own service key, which is part of their cache keys, so each registration caches its own results; an interface registered only keyed is not also registered without one. A catch-all `KeyedService.AnyKey` registration is left uncached. Generated decorators are disposable only when the interface is, so the container no longer keeps transient decorators resolved from the root.

Misconfiguration fails when the generic host starts rather than on the first call: GetOrCreate-only settings on a `[Cache]` method, and in `appsettings.json` invalid values, unknown setting names, a single value for `Tags` or a time span without `:`. `AddMethodCacheFromConfiguration` reads its section then, so configuration sources added after it count from the first call.

OpenTelemetry instruments the unkeyed `ICacheManager` registration and leaves unrelated keyed managers untouched. A keyed-only registration does not satisfy its requirement for a default manager.

OpenTelemetry preserves the original cache manager registration lifetime and DI disposal ownership. Type/factory-created managers remain container-owned; supplied instances remain caller-owned, including asynchronous disposal.

The attribute-move code fix preserves semantic binding across scopes and files: attribute/type aliases are qualified as needed, and constant arguments are materialized before they leave an implementation class.

On C# 13/.NET 9 or newer, generated decorators preserve `allows ref struct` constraints when the cached values and arguments themselves are ordinary cacheable types. Potentially ref-like type parameters used as cache arguments or results receive MC0009 instead of broken generated code. The generator still supports the .NET 8 compiler baseline.

An instrumented cache lookup generates the application cache key once and reuses it for tracing and the presence-aware inner lookup, preserving the original method and arguments without a second serialization/hash pass.

Weighted capacity accounting remains nonnegative even for valid weights near `long.MaxValue`. Before an addition could overflow, the engine evicts existing weight; normal capacity/admission behavior is unchanged below that arithmetic limit.

The web demo uses a shared SQLite database in the temp folder (`methodcache-webdemo-products.db`), so concurrent price updates from either demo instance are atomic. Decimal prices retain full precision. The earlier temporary JSON file is left untouched; the SQLite catalog starts with the demo seed data.

The cache manager has four operations with cancellation last and immutable options. `WithEnabled(false)` bypasses
caching; `WithDistributed(false)` uses only memory. Policy changes retire local generations. Declarative settings
share a stable L2 namespace, while runtime overrides use private namespaces. Invalidation results distinguish local
mutation, confirmed L2 metadata, queued recovery and confirmed/queued backplane sends. See
[configuration](docs/features/configuration.md) and [distributed guarantees](docs/features/distributed-cache.md).

Attribute settings use named arguments, for example `Group`, `Enabled`, `Distributed` and `RetryAfter`. Durations accept invariant positive values such as `30s`, `1.5h` or `00:05:00`. Generated keys always preserve method scope; `CacheKeyAttribute` has been removed. See [configuration](docs/features/configuration.md) and [keys](docs/features/keys.md).

Distributed providers use one container-bound `DistributedCacheOptions.KeyPrefix` for data and invalidation. Value formats and serializer schemas have separate namespaces; metadata retention and atomic epochs prevent older physical variants from reappearing after invalidation. SQL Server uses an explicit versioned schema and delivered migration scripts. See [distributed caching](docs/features/distributed-cache.md).
