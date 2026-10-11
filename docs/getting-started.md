# Getting started

MethodCache is not on nuget.org yet. Until it is, reference the projects from this repository (the
[demo's project file](../samples/MethodCache.Demo/MethodCache.Demo.csproj) shows how). Once published:

```bash
dotnet add package MethodCache
```

The `MethodCache` package contains the library, the source generator and the analyzers. It supports .NET 8, 9 and 10.

## 1. Start from a service

A typical service talks to a database on every call:

<!-- snippet: getting-started-before -->
```cs
public sealed class ProductService(Database db)
{
    public Task<Product> GetAsync(int id, CancellationToken ct) => db.LoadProductAsync(id, ct);

    public Task<List<Product>> SearchAsync(string text, int page) => db.SearchAsync(text, page);

    public Task UpdateAsync(Product product) => db.SaveAsync(product);
}
```
<!-- endSnippet -->

## 2. Put the attributes on its interface

`[Cache]` goes on interface methods that return data, `[CacheInvalidate]` on the methods that change it. The
implementation doesn't change:

<!-- snippet: getting-started-interface -->
```cs
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

- `GetAsync(42)` is stored for ten minutes under a key built from the method and its argument
  (`...IProductService.GetAsync:42`). The `CancellationToken` is not part of the key.
- `"product:{id}"` is a tag template: `GetAsync(42)` is tagged `product:42`.
- `UpdateAsync(42, ...)` removes every entry tagged `products` or `product:42` once it has completed.

The attributes must be on the interface. On a class they do nothing, and the analyzer reports
[MC0001](user-guide/DIAGNOSTICS.md#mc0001) with a code fix that moves them.

## 3. Register it

<!-- snippet: getting-started-registration -->
```cs
builder.Services.AddSingleton<Database>();
builder.Services.AddScoped<IProductService, ProductService>();   // any lifetime
builder.Services.AddMethodCache();                               // after your registrations
```
<!-- endSnippet -->

`AddMethodCache()` wraps every registered interface that has cached methods in its generated decorator and keeps the
lifetime you chose. Call it after your own registrations: a service registered later is not cached. If you don't
register an implementation at all, `AddMethodCache()` registers it as a singleton when it lives in the same project as
the interface.

Generic interfaces work the same way: `IRepository<User>` is cached for every closed type a class in your project
implements, such as `UserRepository : IRepository<User>`. An open generic registration such as
`AddScoped(typeof(IRepository<>), typeof(Repository<>))` is not cached; register the closed types instead.

## What can be cached

<!-- snippet: getting-started-shapes -->
```cs
[Cache] Product Get(int id);                          // synchronous
[Cache] Task<Product> GetAsync(int id);               // Task<T>
[Cache] ValueTask<Product> GetFastAsync(int id);      // ValueTask<T>
[Cache] Task<Product?> FindAsync(string name);        // null results are cached too
```
<!-- endSnippet -->

- Synchronous methods, `Task<T>` and `ValueTask<T>` can be cached. A method that returns nothing has nothing to
  cache, so `[Cache]` on it is a build error ([MCG001](user-guide/DIAGNOSTICS.md#mcg001),
  [MCG002](user-guide/DIAGNOSTICS.md#mcg002)). So is a lazy `IEnumerable<T>` or `IQueryable<T>`
  ([MCG014](user-guide/DIAGNOSTICS.md#mcg014)): return a list.
- Exceptions are never cached: the next call runs the method again.
- Concurrent calls with the same arguments share one call to your method (stampede protection). A caller that cancels
  only stops its own wait.
- `null` results are cached. Turn that off with `CacheNullValues` on the [memory cache](features/memory-cache.md), or
  per method with [conditional caching](features/conditional-caching.md).
- The cached object is returned as is, not copied. Treat cached results as read-only.

## Next steps

- Choose how long entries live: [Expiration](features/expiration.md).
- Remove entries when data changes: [Invalidation](features/invalidation.md).
- Change durations without a redeploy: [Configuration](features/configuration.md).
- Share the cache between instances: [Distributed cache](features/distributed-cache.md).
