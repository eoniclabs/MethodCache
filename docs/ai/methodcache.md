# MethodCache: instructions for coding agents

Copy this file into your project as agent instructions, for example as `.github/copilot-instructions.md`, a Cursor
rule, a Claude Code skill or a section of `AGENTS.md`. It tells a coding agent how to add caching with MethodCache
correctly. Full docs: https://github.com/eoniclabs/MethodCache/blob/main/docs/README.md

Bulk source reads are guarded against key, tag and clear invalidation; invalidated candidates are not published. Collection subclasses with public state are hashed with both that state and their elements. Static tags do not format arguments, and templates format only the parameters they reference.

## Adding caching to a service

1. The `MethodCache` package contains the library, the source generator and the analyzers. Nothing else is needed
   for in-memory caching.
2. Put `[Cache]` on the **interface** method, never on the class. Put `[CacheInvalidate(Tags = [...])]` on the
   interface methods that change the data. Don't change the implementation.
3. Register services as usual, then call `builder.Services.AddMethodCache()` **after** them.

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
  literal and gives warning MCG015; add an `id` parameter instead. Use the same tags on the cached method and on its `[CacheInvalidate]`.
- Put ids (`int`, `Guid`) in tag templates, not free text from users: tags are plain text, so `"1:orders"` in
  `"user:{id}"` equals `"user:{id}:orders"` for id 1. Never pass passwords or tokens as arguments of cached methods.
- Durations are `TimeSpan` strings: `Duration = "00:10:00"`. Other settings: `SlidingExpiration`, `RefreshAhead`
  (shorter than `Duration`), `FailSafe` (or `"off"`), `FailSafeTimeout`, `Version`, `Tags`, and a group name as the
  first argument.
- Cached objects are shared between callers: return immutable types such as records.
- For a type with only private state used as an argument or inside one (a property, a list element), implement
  `ICacheKeyProvider`.
- In library code that registers its own cached interfaces, call `AddMethodCache(null, typeof(SomeLibraryType).Assembly)`
  so the library's assembly is used, not the caller's.
- In `appsettings.json`, write durations as `"00:05:00"` (a bare number is rejected), `Tags` as a list, and only known
  setting names; anything else fails startup.
- For a client from a NuGet package with an interface, use
  `[assembly: CacheInterface(typeof(IClient), nameof(IClient.Method), Duration = "...")]` instead of a wrapper.
- Without an attribute, use `ICacheManager.GetOrCreateAsync(key, factory, o => o.WithDuration(...))` with an explicit,
  prefixed key such as `$"order:{id}"`.
- Fix every `MC` and `MCG` build diagnostic; each links to its explanation, and many have a code fix.
- In tests, register `NoOpCacheManager` (namespace `MethodCache.Extensibility`) after `AddMethodCache()` to turn
  caching off.
- To invalidate a `GetOrCreateAsync` entry that has a version, pass `CacheManagerExtensions.GetCacheKey(key, version)`
  to `InvalidateByKeysAsync`.
- For a distributed cache, use `AddMethodCacheRedis` or `AddMethodCacheSqlServer`. A custom `IDistributedCache` must
  also implement `IAtomicInvalidationStore` (`MethodCache.Extensibility`), or the app fails at startup.
- With `AddHttpCaching()`, send cookies as explicit `Cookie` headers; it turns automatic cookies off.

## Namespaces

- `MethodCache`: `[Cache]`, `[CacheInvalidate]`, `[CacheInterface]`, `ICacheManager`, `ICacheAdministrator`,
  `IRuntimeCacheConfigurator`, `MethodCacheKeys`, `MethodCacheMemoryOptions`, `Any<T>`, `ICacheKeyProvider`.
- `MethodCache.Extensibility`: `ISerializer`, `JsonCacheSerializer`, `ICacheKeyGenerator`, `JsonKeyGenerator`,
  `NoOpCacheManager`, `IAtomicInvalidationStore`.
- `Microsoft.Extensions.DependencyInjection`: `AddMethodCache`, `AddMethodCacheFromConfiguration`,
  `AddMethodCacheRedis` (package `MethodCache.Redis`), `AddMethodCacheSqlServer` (package `MethodCache.SqlServer`).
