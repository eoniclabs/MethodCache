# MethodCache

[![Build Status](https://github.com/eoniclabs/MethodCache/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/eoniclabs/MethodCache/actions/workflows/ci.yml)
[![Coverage](https://codecov.io/gh/eoniclabs/MethodCache/graph/badge.svg?branch=main)](https://codecov.io/gh/eoniclabs/MethodCache)
[![.NET](https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![NuGet](https://img.shields.io/badge/NuGet-not%20yet%20published-lightgrey)](https://www.nuget.org/packages?q=MethodCache)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**Cache any .NET method with one attribute.** MethodCache's source generator writes the caching code for you at
compile time: no reflection, no hand-written keys, no `TryGetValue` boilerplate, and it works with Native AOT.

```csharp
public interface IUserService
{
    [Cache(Duration = "00:10:00", Tags = ["user:{id}"])]
    Task<User> GetUserAsync(int id);

    [CacheInvalidate(Tags = ["user:{id}"])]
    Task RenameAsync(int id, string name);
}

builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddMethodCache();
```

That's the whole setup. `GetUserAsync(42)` runs once and then comes from memory until it expires or
`RenameAsync(42, ...)` invalidates user 42, and only user 42.

## Why MethodCache

- **One attribute, zero boilerplate.** Keys are built from the method and its arguments; your service stays plain code.
- **Fast.** A cache hit allocates nothing, is as fast as hand-written `IMemoryCache` code, and costs 4 to 6 times less than HybridCache or FusionCache ([numbers](#performance)).
- **Compile-time, not runtime.** A Roslyn source generator writes the decorators, analyzers catch mistakes in the editor, and Native AOT is tested in CI.
- **Tags that understand your arguments.** `"user:{id}"` becomes `user:42`, so an update invalidates exactly what it changed.
- **Tune it without redeploying.** Durations and tags come from attributes, a global default, `appsettings.json` (reloaded live) or runtime overrides.
- **Third-party clients too.** One line caches chosen methods of an SDK or API client's own interface, no wrapper needed ([how](#caching-code-you-dont-own)).
- **HTTP responses as well.** `AddHttpCaching()` on an `HttpClient` honors `Cache-Control` and revalidates with ETags ([how](#http-responses)).
- **One line to go distributed.** `AddMethodCacheRedis(...)` shares results between instances and keeps every instance's memory cache in sync.
- **Built for production.** Stampede protection, fail-safe, a W-TinyLFU memory cache, admin endpoints, a health check and OpenTelemetry tracing and metrics.

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

```csharp
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

</td><td>

```csharp
[Cache(Duration = "00:10:00", Tags = ["user:{id}"])]
Task<User> GetUserAsync(int id);

[CacheInvalidate(Tags = ["user:{id}"])]
Task RenameAsync(int id, string name);
```

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

## Features

### Invalidation

Invalidate by tag with `[CacheInvalidate]`, or from code by tag, key or pattern:

```csharp
await cache.InvalidateByTagsAsync("user:42");
await cache.InvalidateByKeysAsync(MethodCacheKeys.For(typeof(IUserService), nameof(IUserService.GetUserAsync), MethodCacheKeys.Part(42)));
await cache.InvalidateByTagPatternAsync("tenant:7:*");   // in-memory only
```

Generated keys look like `MyApp.IUserService.GetUserAsync:42`, and `MethodCacheKeys` builds the same keys, so you
never guess the format.

### Configuration without redeploying

Each setting comes from the highest layer that sets it: runtime overrides, then `appsettings.json`, then startup code,
then the attribute. Global and group defaults fill in what a method leaves unset.

```csharp
builder.Services.AddMethodCache(config =>
    config.DefaultPolicy(o => o.WithDuration(TimeSpan.FromMinutes(5))));
builder.Services.AddMethodCacheFromConfiguration(builder.Configuration);
```

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
policy in code, and the change applies to the next call. See the [Configuration Guide](docs/user-guide/CONFIGURATION_GUIDE.md).

### Distributed cache

```csharp
builder.Services.AddMethodCache();
builder.Services.AddMethodCacheRedis("localhost:6379");       // or AddMethodCacheSqlServer(connectionString)
```

Results live in memory and in Redis (or SQL Server, or any `IDistributedCache` via `AddMethodCacheDistributedCache()`).
An in-memory miss reads the distributed cache before running your method, and invalidations reach every instance.
Values are JSON by default; `AddMethodCacheMessagePack()` makes them smaller.

### Caching code you don't own

To cache a client from a NuGet package, name its interface and the methods to cache:

```csharp
[assembly: CacheInterface(typeof(IFxClient), nameof(IFxClient.GetRateAsync),
    Duration = "00:01:00", Tags = ["fx", "fx:{from}"])]

builder.Services.AddSingleton<IFxClient, FxClient>();   // the vendor's client, registered as usual
builder.Services.AddMethodCache();
```

`GetRateAsync` now behaves like a `[Cache]` method: keys from the arguments, tags, `appsettings.json` and runtime
overrides. Methods you don't list, such as `PlaceOrderAsync`, always reach the client. For a client without an
interface, wrap the calls in an interface of your own with `[Cache]`, or use the same engine with a key you choose:

```csharp
var rate = await cache.GetOrCreateAsync(
    $"fx:{from}:{to}",
    async (ctx, ct) => await client.GetRateAsync(from, to),
    o => o.WithDuration(TimeSpan.FromMinutes(1)).WithTags("fx"));
```

See the [Third-party Caching Guide](docs/user-guide/THIRD_PARTY_CACHING.md).

### HTTP responses

When the API sends caching headers, let `HttpClient` use them instead of choosing a duration yourself:

```csharp
builder.Services.AddHttpClient<IGitHubClient, GitHubClient>()
    .AddHttpCaching();   // MethodCache.HttpCaching
```

Responses are cached as `Cache-Control` allows and revalidated with `ETag` / `Last-Modified`, so a `304 Not Modified`
reaches your code as the full cached response. A `POST`, `PUT`, `PATCH` or `DELETE` drops the cached responses for
that URL. With `UseDistributedCache`, all instances share the responses through the same Redis or SQL Server.
See the [package README](src/MethodCache.HttpCaching/README.md).

### Monitoring

```csharp
builder.Services.AddHealthChecks().AddMethodCache();
app.MapMethodCacheAdmin().RequireAuthorization("CacheAdmins");   // MethodCache.AspNetCore
builder.Services.AddMethodCacheOpenTelemetry();                  // MethodCache.OpenTelemetry
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddMethodCacheInstrumentation())
    .WithMetrics(m => m.AddMethodCacheInstrumentation());
```

The admin endpoints show hits, misses, live entries and every method's effective policy, and can invalidate by tag.
The same operations are available in code through `ICacheAdministrator`.

### Native AOT

Registration and keys use no reflection. Under Native AOT, register the JSON serializer with your
`JsonSerializerContext`:

```csharp
builder.Services.AddSingleton<ISerializer>(new JsonCacheSerializer(AppJsonContext.Default.Options));
```

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

- [Configuration Guide](docs/user-guide/CONFIGURATION_GUIDE.md)
- [Fluent API Guide](docs/user-guide/FLUENT_API.md)
- [Third-party Caching Guide](docs/user-guide/THIRD_PARTY_CACHING.md)
- [Build Diagnostics](docs/user-guide/DIAGNOSTICS.md)
- [Adding caching to an existing service](docs/migration/ADDING_CACHING_TO_EXISTING_SERVICE.md)
- [Migrating from IMemoryCache](docs/migration/MIGRATION_FROM_IMEMORYCACHE.md)
- Package guides: [Redis](src/MethodCache.Redis/README.md), [SQL Server](src/MethodCache.SqlServer/README.md), [ASP.NET Core](src/MethodCache.AspNetCore/README.md), [OpenTelemetry](src/MethodCache.OpenTelemetry/README.md), [HTTP caching](src/MethodCache.HttpCaching/README.md)
- [Demo](samples/MethodCache.Demo/) and [web demo](samples/MethodCache.WebDemo/)

## Contributing

Issues and pull requests are welcome. Before you open a pull request, run:

```bash
dotnet format
dotnet test MethodCache.sln
```

See [DEVELOPMENT_GUIDE.md](DEVELOPMENT_GUIDE.md) for the repository layout.

## License

[MIT](LICENSE)
