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
- **Fast.** A cache hit allocates nothing and on x64 is about 10 % faster than hand-written `IMemoryCache` code, faster than HybridCache and 8 to 9 times faster than FusionCache ([numbers](#performance)).
- **Compile-time, not runtime.** A Roslyn source generator writes the decorators, analyzers catch mistakes in the editor, and Native AOT is tested in CI.
  Previously compiled contract-1 vendor decorators keep working without regeneration, with both inline and materialized keys.
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

For ASP.NET Core with admin endpoints, OpenTelemetry and two instances sharing Redis, see the
[web demo](samples/MethodCache.WebDemo/).
The web demo opens its admin endpoints in Development; in Production they require authentication and the `Admin` role.
Its price endpoint rejects missing, null and negative prices with HTTP 400, preserving stored and cached data.
Its SQLite data can be reset, and query counters show which requests actually reach the database.

Chapter 11 of the [console tour](samples/MethodCache.Demo/#httpclient-caching) starts a local HTTP server and
checks `Cache-Control`, ETag/304, `no-store` and invalidation after PUT in the same run.
See the [console guide](samples/MethodCache.Demo/) for all eleven chapters and the
[web guide](samples/MethodCache.WebDemo/) for automated two-instance Redis and outage scenarios.

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

MethodCache 1.0 is a release candidate on nuget.org, so add `--prerelease` until 1.0.0 is out (the same goes for
the add-on packages):

```bash
dotnet add package MethodCache --prerelease
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

Generated keys look like `[MyApp]MyApp.IUserService.GetUserAsync:42` (a record argument as `(alice:2)`, an array or list as
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
builder.Services.AddMethodCache(methodCache => methodCache.FromConfiguration(builder.Configuration));
```
<!-- endSnippet -->

```json
{
  "MethodCache": {
    "Services": {
      "[MyApp]MyApp.IUserService.GetUserAsync": { "Duration": "00:30:00" }
    }
  }
}
```

Edits to `appsettings.json` apply while the app runs. During an incident, `IRuntimeCacheConfigurator` changes a
policy in code, and the change applies to the next call. See [Configuration](docs/features/configuration.md).

### Distributed cache

<!-- snippet: readme-distributed -->
```cs
builder.Services.AddMethodCache(mc => mc.UseRedis("localhost:6379"));       // or mc.UseSqlServer(connectionString)
```
<!-- endSnippet -->

Results live in memory and in Redis (or SQL Server, or your own cache, see
[Distributed cache](docs/features/distributed-cache.md)).
An in-memory miss reads the distributed cache before running your method, and invalidations reach every instance.
Values are JSON by default; a method whose results JSON can't read back unchanged (private setters, a derived type,
`object`) is kept in memory only, with a warning. `mc.UseMessagePack()` makes values smaller.
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
that URL. `AddHttpCaching` requires `AddMethodCache`. Configured native Redis or SQL Server stores HTTP responses
exclusively in MethodCache's owned L2; otherwise Core uses its separate `MaxHttpBytes` budget (100 MiB by default).
Shared atomic generations prevent late old responses becoming visible after a confirmed mutation, even without
backplane delivery. Graceful async disposal drains HTTP invalidation recovery and reports any unconfirmed targets
as a count; recovery does not survive process exit. `KeepCookies=true` preserves transport cookies and bypasses response caching; otherwise
`AddHttpCaching()` turns off automatic cookies
on the client's handler; send any cookie the API needs as an explicit `Cookie` header. Redirects the transport
follows are not stored under the URL you asked for, retries (Polly, standard resilience) get full responses, and a
`GET` with a body goes to the origin.
Local client-tag invalidation reclaims matching bodies with a scan of resident HTTP entries; its work grows with the entry count.
See [HTTP response caching](docs/features/http-caching.md) and the [package README](src/MethodCache.HttpCaching/README.md).

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

Memory-only asynchronous misses avoid allocating closures for inactive fail-safe and distributed-write branches.
HTTP responses without custom key headers or `Vary` fields use direct variant selection; private caches still
isolate `Authorization` and `Cookie`.
SQL Server reuses command text for repeated generation batch shapes while reading every generation from shared storage.

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
| Other instances drop their memory copy | ✅ Redis (pub/sub), SQL Server (polling, ~1 s) | ❌ | ✅ |
| Tag invalidation | ✅ with argument templates | ✅ | ✅ |
| Change a method's policy from appsettings.json or at runtime | ✅ | ❌ | ❌ |
| Admin endpoints and health check | ✅ | ❌ | ❌ |

HybridCache ships with .NET and is the natural choice when you want a plain `GetOrCreateAsync` API and nothing more.
FusionCache is more mature and has more resilience features, such as fail-safe in the distributed cache and
auto-recovery. MethodCache is for when you want caching to be a line of metadata on the method rather than code inside it.

## Packages

Requires .NET 10 and the .NET 10 SDK. The pre-1.0 packages no longer target .NET 8 or 9.

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
- [Console tour, including HttpClient caching](samples/MethodCache.Demo/) and [web demo](samples/MethodCache.WebDemo/)
- Using a coding agent? Give it [docs/ai/methodcache.md](docs/ai/methodcache.md). [llms.txt](llms.txt) and [llms-full.txt](llms-full.txt) have the docs for language models, and `context7.json` lets Context7 index them.

## Additional behavior

Redis backplane recovery retries transient unsubscribe/subscribe failures after reconnect, with a delay that doubles from one second to at most 30 seconds. It reports connected only after subscription acknowledgement and the local recovery clear; shutdown cancels pending retries.

MessagePack keys preserve derived member data in nested base-class/interface slots and collection elements. Earlier keys for these arguments change; existing entries become cold misses and expire normally.

Manual `TryGetAsync` reads a fresh entry, including a cached null, even when the fast path is disabled or refresh-ahead is due. This lookup does not start a factory or background refresh.

Overloaded generic methods include generic arity in their policy ids and key prefixes: `Get(int)`, `Get``1(int)` and `Get``2(int)` remain distinct. Generator and runtime configuration use the same ids; earlier keys for overloaded generic methods become cold misses.

Caller cancellation stops the wait during an L2 read or a slow factory, including bulk reads and fail-safe waits. L2 reads pass cancellation to the backend; shared factory work may finish for other callers. Caller cancellation is not reported as a dependency failure.

Bulk reads guard their source work against key, tag and clear invalidation: an invalidated batch result is returned to its caller but is not published. Collection subclasses keep their own public properties and fields in hashed keys, as well as their elements. Static tags do not format arguments; templates format only the parameters they reference. Redis backplane publications honor caller cancellation, and a cancelled unsubscribe acknowledgement does not fail application shutdown. SQL backplane gaps never hold newer invalidations behind a locked transaction, and HTTP variant schema changes retire their publication epoch once; a failed deletion after a successful rotation does not retire a newer concurrent publication.

Published policy tags are owned, read-only snapshots. Mutating the collection supplied by application code cannot change a published policy or bypass its version/change notifications.

HTTP cache freshness includes request/response delay when correcting the upstream Age header, for both initial responses and 304 validation. Age arithmetic saturates instead of overflowing on extreme headers.

Background HTTP revalidation preserves the request HTTP version, version policy and a shallow copy of its request options. Option values keep their original ownership; use values that remain valid for background work.

A keyed `ICacheManager` registration remains independent of the default unkeyed manager. Registering another service key does not suppress `AddMethodCache()` or its default administration services.

Keyed registrations of cached interfaces are decorated under their own service key, which is part of their cache keys, so each registration caches its own results; an interface registered only keyed is not also registered without one. A catch-all `KeyedService.AnyKey` registration is rejected with a named binding error; register concrete keys instead. Generated decorators are disposable only when the interface is, so the container no longer keeps transient decorators resolved from the root.

Misconfiguration fails when the generic host starts rather than on the first call: invalid values, unknown setting names, a single value for `Tags` or an invalid duration in `appsettings.json`. Positive invariant unit durations such as `30s` are accepted. `AddMethodCache(cache => cache.FromConfiguration(configuration))` reads its section at startup, so configuration sources added later apply from the first call.

The attribute-move code fix preserves semantic binding across scopes and files: attribute/type aliases are qualified as needed, and constant arguments are materialized before they leave an implementation class.

With C# 13 or newer, generated decorators preserve `allows ref struct` constraints when the cached values and arguments themselves are ordinary cacheable types. Potentially ref-like type parameters used as cache arguments or results receive MC0009 instead of broken generated code. The packages require .NET 10 and the .NET 10 SDK. C# 12 source remains supported when explicitly selected in that SDK.

Long namespace and type names work with `EmitCompilerGeneratedFiles`; generated files use short, stable names when needed.

Weighted capacity accounting remains nonnegative even for valid weights near `long.MaxValue`. Before an addition could overflow, the engine evicts existing weight; normal capacity/admission behavior is unchanged below that arithmetic limit.

The web demo uses a shared SQLite database in the temp folder (`methodcache-webdemo-products.db`), so concurrent price updates from either demo instance are atomic. Decimal prices retain full precision. The earlier temporary JSON file is left untouched; the SQLite catalog starts with the demo seed data.

The cache manager has four operations with cancellation last and immutable options. `WithEnabled(false)` bypasses
caching; `WithDistributed(false)` uses only memory. Policy changes retire local generations. Declarative settings
share a stable L2 namespace, while runtime overrides use private namespaces. Invalidation results distinguish local
mutation, confirmed L2 metadata, queued recovery and confirmed/queued backplane sends. See
[configuration](docs/features/configuration.md) and [distributed guarantees](docs/features/distributed-cache.md).

Attribute settings use named arguments, for example `Group`, `Enabled`, `Distributed` and `FailSafeRetryAfter`. Durations accept invariant positive values such as `30s`, `1.5h` or `00:05:00`. Generated keys always preserve method scope; `CacheKeyAttribute` has been removed. See [configuration](docs/features/configuration.md) and [keys](docs/features/keys.md).

Distributed providers use one container-bound `DistributedCacheOptions.KeyPrefix` for data and invalidation. Value formats and serializer schemas have separate namespaces; metadata retention and atomic epochs prevent older physical variants from reappearing after invalidation. SQL Server uses an explicit versioned schema and delivered migration scripts. See [distributed caching](docs/features/distributed-cache.md).

Core emits standard `MethodCache` metrics and activities. The optional OpenTelemetry package subscribes to them;
cache keys/tags require explicit Core options. [Monitoring](docs/features/monitoring.md) documents the instruments,
local method/HTTP statistics, configured weight units and independent storage/backplane health.

Dictionary hashes are process-local implementation metadata. Canonical key text, L2 identities, administration and
invalidation remain stable across processes and restarts; the lookup hash is never stored or transmitted.

Logical-key lookups and invalidations use the same framing and optional version as storage, including keys starting
with NUL. Admin mutations return 503 while distributed invalidation awaits confirmation; recovery remains owned by
the cache. Incompatible distributed scope contracts use memory only and report an actionable unhealthy status.
See [manual keys](docs/features/manual-caching.md#versions-and-the-stored-key) and [monitoring](docs/features/monitoring.md).
