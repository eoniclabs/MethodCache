# MethodCache: instructions for coding agents

Copy this file into your project as agent instructions, for example as `.github/copilot-instructions.md`, a Cursor
rule, a Claude Code skill or a section of `AGENTS.md`. It tells a coding agent how to add caching with MethodCache
correctly. Full docs: https://github.com/eoniclabs/MethodCache/blob/main/docs/README.md. The `MethodCache` package
carries the same docs in its `docs` folder (`~/.nuget/packages/methodcache/<version>/docs/`): `how-to.md` answers
common questions and `reference/` has the exact rules.

## Adding caching to a service

1. The `MethodCache` package contains the library, the source generator and the analyzers. Nothing else is needed
   for in-memory caching. All packages target .NET 10; install add-ons at the same version as `MethodCache`.
2. Put `[Cache]` on the **interface** method, never on the class. Put `[CacheInvalidate(Tags = [...])]` on the
   interface methods that change the data. Don't change the implementation.
3. Register services as usual, then call `builder.Services.AddMethodCache()` **after** them. Later cacheable
   registrations fail at host startup. A cached interface without a registration needs
   `AutoRegister(lifetime)` and exactly one class that implements it.
4. Use `MethodCacheBuilder` for startup, in one `AddMethodCache(mc => mc...)` call: `Defaults`, `Group`, typed
   `Method`, `Memory`, `Distributed`, `Telemetry`, `FromConfiguration`, `FromAssemblyOf<T>()` and the integrations'
   `UseRedis` / `UseSqlServer` / `UsePostgreSql` (one of them) / `UseMessagePack`. Don't use `services.Configure<...>` for MethodCache
   options.

<!-- snippet: getting-started-interface -->
```cs
using MethodCache;

public interface IProductService
{
    [Cache(Duration = "00:10:00", Tags = ["products", "product:{id}"])]
    Task<Product> GetAsync(int id, CancellationToken ct = default);

    [Cache(Duration = "00:01:00", Tags = ["products"])]
    Task<List<Product>> SearchAsync(string text, int page);

    [CacheInvalidate(Tags = ["products", "product:{id}"])]
    Task UpdateAsync(int id, Product product);
}

public sealed class ProductService(Database db) : IProductService
{
    public Task<Product> GetAsync(int id, CancellationToken ct = default) => db.LoadProductAsync(id, ct);

    public Task<List<Product>> SearchAsync(string text, int page) => db.SearchAsync(text, page);

    public Task UpdateAsync(int id, Product product) => db.SaveAsync(product);
}
```
<!-- endSnippet -->

<!-- snippet: getting-started-registration -->
```cs
builder.Services.AddSingleton<Database>();
builder.Services.AddScoped<IProductService, ProductService>();   // any lifetime
builder.Services.AddMethodCache();                               // after your registrations
```
<!-- endSnippet -->

## Rules

- Only interface methods that return a value can be cached: sync, `Task<T>` or `ValueTask<T>`. Return a list, never
  a lazy `IEnumerable<T>` or `IQueryable<T>`.
- Callers must use the interface. Calling the method from inside the class, or resolving the class itself, bypasses
  the cache.
- Keys are generated from the method and its arguments; don't write keys for `[Cache]` methods. A `CancellationToken`
  is not part of the key. Every value that changes the result must be a parameter.
- Tags take templates with a **parameter name** only: `"product:{id}"`. `"{product.Id}"` is not a template, stays
  literal and gives warning MC0016; add an `id` parameter instead. Use the same tags on the cached method and on its `[CacheInvalidate]`.
- Tag values are formatted like `MethodCacheKeys.Part`: `:` and `\` in strings are escaped, a `Guid` is written
  without hyphens, an enum as its number, `bool` as `true`/`false`, numbers and dates in the invariant culture. Prefer
  ids (`int`, `Guid`) and never pass passwords or tokens as arguments of cached methods.
- `[CacheInvalidate]` runs after the method, or its task, succeeds; never when it throws.
- Synchronous `[Cache]` methods use the memory cache only, also with Redis, SQL Server or PostgreSQL. Make a method
  async to share its results between instances.
- Durations accept invariant `[d.]hh:mm:ss` or unit strings such as `30s` and `1.5h`. Other settings: `SlidingExpiration` (or `"off"`), `RefreshAhead`
  (shorter than `Duration` and `SlidingExpiration`, or `"off"`; MC0023 otherwise), `FailSafe` (or `"off"`), `FailSafeTimeout`, `FailSafeRetryAfter`,
  `Version`, `Tags`, `Enabled`, `Distributed` and the named `Group` setting. In code each has one builder method with
  the same name: `WithFailSafe(d)`, `WithFailSafeTimeout(t)`, `WithFailSafeRetryAfter(t)` and so on.
- Cached objects are shared between callers: return immutable types such as records.
- For a type with only private state used as an argument or inside one (a property, a list element), implement
  `ICacheKeyProvider`.
- For library modules and Native AOT, select the containing assembly with `MethodCacheBuilder.FromAssemblyOf<T>()`.
  If multiple modules generate the same interface, the selected one wins; the other modules' services stay cached, and
  calling/entry assemblies have no special priority.
- In `appsettings.json`, the `MethodCache` section holds `Defaults`, `FailSafe`, `Groups` (by group name) and
  `Methods` (by method id, such as `[MyApp]MyApp.IProductService.GetAsync`; not `Services`). Write durations as
  `"00:05:00"` (a bare number is rejected), `Tags` as a list, and only known setting names. Anything else, an
  unknown method id and a method listed twice fail startup.
- `CacheWhen` is set for one method in code (`mc.Method(...).CacheWhen(...)`); in `Defaults` or a group it fails
  startup, and so does a `CacheWhen<T>` for a type the method can't return.
- A `Duration` may be at most `MethodCacheMemoryOptions.MaxExpiration` (7 days by default, at most 365); a longer one
  fails startup. Raise it with `mc.Memory(o => o.MaxExpiration = ...)`.
- For a client from a NuGet package with an interface, use
  `[assembly: CacheInterface(typeof(IClient), nameof(IClient.Method), Duration = "...")]` instead of a wrapper.
- Without an attribute, use `ICacheManager.GetOrCreateAsync(key, factory, o => o.WithDuration(...))` with an explicit,
  prefixed key such as `$"order:{id}"`.
- Fix MethodCache's `MC` build diagnostics; each links to its explanation, and many have a code fix.
- In tests, register `NoOpCacheManager` (namespace `MethodCache`) after `AddMethodCache()` to turn method caching
  off. When a test writes to the database directly, call `ICacheAdministrator.ClearAsync()` after the write.
- To invalidate a `GetOrCreateAsync` entry that has a version, pass the key and the version:
  `InvalidateKeyAsync(key, 2)`. Never pass a stored key (`MethodCacheKeys.StoredKey`) to `ICacheManager`.
- To invalidate one call of a `[Cache]` method, get its key from `IMethodCacheKeys`:
  `cache.InvalidateKeyAsync(keys.For<IProductService>(s => s.GetAsync(42)))`. For every entry of one method, use
  `ICacheAdministrator.InvalidateMethodAsync(keys.MethodId<IProductService>(s => s.GetAsync(0)))`.
- When data changes outside the cached interface (an endpoint or command writing to the database, or after a
  transaction commits), invalidate after the write with a tag from `IMethodCacheKeys.Tag`:
  `cache.InvalidateTagAsync(keys.Tag<IPriceSource>(s => s.GetRateAsync(provider, model), IPriceSource.RateTag))`.
  Never build a tag with string interpolation (`$"price:{model}"`): tags escape `:` and `\` in strings and write a
  `Guid` without hyphens and an enum as its number, so a hand-built tag can silently match nothing (MC0024).
- Several registrations of one interface with different implementation types each cache their own results.
- For a distributed cache use `UseRedis`, `UseSqlServer` or `UsePostgreSql`; MethodCache never uses the app's
  `IDistributedCache`. Values are JSON; use `UseJson(context)` for Native AOT and `UseJson(options)` for your own
  settings. A result type that doesn't read back unchanged (private setters, a derived type) stays in memory, with a
  warning: return records with public properties.
- `AddHttpCaching()` needs `AddMethodCache()` in the same container, in either order. It turns off automatic cookies;
  send cookies as a `Cookie` header, or set `KeepCookies = true`, which turns response caching off.
- Generated keys and method ids start with the service assembly's name in brackets. Never write a key or method id by
  hand: get keys from `IMethodCacheKeys.For`, tags from `IMethodCacheKeys.Tag` and ids from `IMethodCacheKeys.MethodId`.
- Telemetry: `MethodCacheTelemetryOptions` (`mc.Telemetry(...)`) records keys and tags on spans only when you turn it
  on, hashed by default. `AddMethodCacheInstrumentation()` (package `MethodCache.OpenTelemetry`) subscribes
  OpenTelemetry to the `MethodCache` meter and activity source.

## Namespaces

- `MethodCache`: `[Cache]`, `[CacheInvalidate]`, `[CacheInterface]`, `ICacheManager`, `ICacheAdministrator`,
  `IRuntimeCacheConfigurator`, `IMethodCacheKeys`, `MethodCacheKeys`, `MethodCacheMemoryOptions`, `Any<T>`,
  `ICacheKeyProvider`.
- `MethodCache`: also `NoOpCacheManager` (sealed).
- `Microsoft.Extensions.DependencyInjection`: `AddMethodCache`. Everything else is a method on its builder:
  `FromConfiguration`, `Distributed(...)`, `Telemetry(...)`, `UseRedis` (package `MethodCache.Redis`), `UseSqlServer`
  (package `MethodCache.SqlServer`), `UsePostgreSql` (package `MethodCache.PostgreSql`) and `UseMessagePack` (package `MethodCache.Serialization.MessagePack`).
