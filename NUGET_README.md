# MethodCache

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

Register your services as usual and call `AddMethodCache()` after them. `GetUserAsync(42)` then runs once and comes
from memory until it expires or `RenameAsync(42, ...)` invalidates user 42, and only user 42.

- **One attribute, zero boilerplate.** Keys are built from the method and its arguments; your service stays plain code.
- **Fast.** A cache hit allocates nothing and on x64 is about 10 % faster than hand-written `IMemoryCache` code.
- **Compile-time.** A source generator writes the decorators, analyzers catch mistakes in the editor, and it works
  with Native AOT.
- **Tags that understand your arguments.** `"user:{id}"` becomes `user:42`, so an update invalidates exactly what it
  changed.
- **Tune it without redeploying.** Durations and tags come from attributes, defaults, `appsettings.json` (reloaded
  live) or runtime overrides.
- **Third-party clients, HTTP responses, distributed caches.** Cache an SDK client's own interface, `HttpClient`
  responses (`MethodCache.HttpCaching`), and share results through Redis, SQL Server or PostgreSQL
  (`MethodCache.Redis`, `MethodCache.SqlServer`, `MethodCache.PostgreSql`). Install add-ons at the same version.
- **Built for production.** Stampede protection, fail-safe, a size-bounded memory cache, admin endpoints, a health
  check and OpenTelemetry tracing and metrics.

## Common questions

- **Why does it need an interface?** The generator writes a class that implements your interface and checks the cache
  before it calls your implementation. Without an interface, use `ICacheManager.GetOrCreateAsync` with a key you choose.
- **When does `[CacheInvalidate]` run?** After the method, or its task, succeeds. Never when it throws.
- **The data changes somewhere else.** Invalidate after the write with
  `cache.InvalidateTagAsync(keys.Tag<IPriceSource>(s => s.GetRateAsync(provider, model), "price:{provider}:{model}"))`.
  `keys.Tag` formats the tag exactly as the cached method does.
- **Can I build the tag string myself?** Only with `MethodCacheKeys.Part` around each value: a `Guid` is written
  without hyphens, an enum as its number, and `:` is escaped. Analyzer MC0024 warns about hand-built tags.
- **How do I cache null, or skip some results?** `CacheNullValues` sets it for every method; `CacheWhen` decides per
  method which results to store.
- **A test writes to the database directly.** Call `ICacheAdministrator.ClearAsync()` after the write, or register
  `NoOpCacheManager` to turn caching off.
- **Are synchronous methods cached?** In memory only, also with a distributed cache.
- **How do I see what's cached?** `ICacheAdministrator`, the admin endpoints in `MethodCache.AspNetCore`, and the
  `MethodCache` meter.

More in [How do I…?](https://github.com/eoniclabs/MethodCache/blob/main/docs/how-to.md).

## Documentation

This package carries its documentation in its `docs` folder
(`~/.nuget/packages/methodcache/<version>/docs/`), so a coding agent can read the docs for the version you
installed. Start with `docs/ai/methodcache.md` and `docs/how-to.md`. Online:
[documentation](https://github.com/eoniclabs/MethodCache/blob/main/docs/README.md), [getting started](https://github.com/eoniclabs/MethodCache/blob/main/docs/getting-started.md),
[build diagnostics](https://github.com/eoniclabs/MethodCache/blob/main/docs/user-guide/DIAGNOSTICS.md) and
[llms-full.txt](https://raw.githubusercontent.com/eoniclabs/MethodCache/main/llms-full.txt).

To have an agent add caching, give it the prompt in
[Using a coding agent](https://github.com/eoniclabs/MethodCache#using-a-coding-agent).

Source, benchmarks and a runnable demo: https://github.com/eoniclabs/MethodCache
