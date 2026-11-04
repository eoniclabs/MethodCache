[← MethodCache documentation](README.md)

# Getting started

MethodCache 1.0 is a release candidate on nuget.org, so add `--prerelease` until 1.0.0 is out (the same goes for
the add-on packages):

```bash
dotnet add package MethodCache --prerelease
```

The `MethodCache` package contains the library, the source generator and the analyzers. It requires .NET 10 and the
.NET 10 SDK, as every add-on package does.

## 1. Start from a service

A typical service talks to a database on every call:

<!-- snippet: getting-started-before -->
```cs
public sealed class ProductService(Database db)
{
    public Task<Product> GetAsync(int id, CancellationToken ct = default) => db.LoadProductAsync(id, ct);

    public Task<List<Product>> SearchAsync(string text, int page) => db.SearchAsync(text, page);

    public Task UpdateAsync(int id, Product product) => db.SaveAsync(product);
}
```
<!-- endSnippet -->

## 2. Put the attributes on its interface

`[Cache]` goes on interface methods that return data, `[CacheInvalidate]` on the methods that change it. The class
only gains `: IProductService`; its methods don't change:

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

- `GetAsync(42)` is stored for ten minutes under a key built from the method and its argument
  (`...IProductService.GetAsync:42`). The `CancellationToken` is not part of the key.
- `"product:{id}"` is a tag template: `GetAsync(42)` is tagged `product:42`.
- `UpdateAsync(42, ...)` removes every entry tagged `products` or `product:42` once it has completed.

> [!IMPORTANT]
> The attributes must be on the interface. On a class they do nothing, and the analyzer reports
> [MC0001](user-guide/DIAGNOSTICS.md#mc0001) with a code fix that moves them.

## 3. Register it

<!-- snippet: getting-started-registration -->
```cs
builder.Services.AddSingleton<Database>();
builder.Services.AddScoped<IProductService, ProductService>();   // any lifetime
builder.Services.AddMethodCache();                               // after your registrations
```
<!-- endSnippet -->

`AddMethodCache()` wraps every registered interface that has cached methods in its generated decorator and keeps the
lifetime you chose. Register your cached services before it: a cached service registered after it fails at startup
with an error that says so.

- Keyed registrations (`AddKeyedScoped<IProductService, ProductService>("primary")`) are cached too, each with its own
  entries.
- Several registrations of one interface with different classes, resolved as `IEnumerable<IProductService>`, each cache
  their own results.
- Generic interfaces such as `IRepository<User>` are cached for each closed type a class implements.
- A service you haven't registered is registered only when you ask: `AddMethodCache(mc => mc.AutoRegister(ServiceLifetime.Scoped))`.

[Registration](reference/registration.md) has the exact rules, including open generic registrations and several
assemblies that generate the same interface.

### Registering with a factory

A factory registration is cached like any other: register it before `AddMethodCache()`, and the decorator wraps
what your factory returns, with the lifetime you chose.

<!-- snippet: getting-started-factory -->
```cs
builder.Services.AddScoped<IProductService>(
    sp => new ProductService(sp.GetRequiredService<Database>()));
builder.Services.AddMethodCache();
```
<!-- endSnippet -->

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
  cache, so `[Cache]` on it is a build error ([MC0003](user-guide/DIAGNOSTICS.md#mc0003),
  [MC0022](user-guide/DIAGNOSTICS.md#mc0022)). So is a lazy `IEnumerable<T>` or `IQueryable<T>`
  ([MC0015](user-guide/DIAGNOSTICS.md#mc0015)): return a list.
- Synchronous methods use the memory cache only, also with a distributed cache configured
  ([Generated decorators](features/source-generator.md#synchronous-methods)).
- Exceptions are never cached: the next call runs the method again.
- Concurrent calls with the same arguments share one call to your method (stampede protection). A caller that cancels
  only stops its own wait.
- Without a `Duration`, entries live five minutes (the built-in default) unless you set a
  default ([Expiration](features/expiration.md#duration)).
- `null` results are cached. To skip null for one method, give it a
  [condition](features/conditional-caching.md) (`CacheWhen(x => x is not null)`); `CacheNullValues = false` on the
  [memory cache](features/memory-cache.md) turns it off for every method.
- The cached object is returned as is, not copied. Treat cached results as read-only.

## Per-user data

A cached result is shared by every caller with the same arguments. The key is built from the arguments only, so
anything else the result depends on must be an argument too:

- **The signed-in user or tenant.** Pass the id: `GetOrdersAsync(int userId)`, not a method that reads the user from
  `IHttpContextAccessor` or a `ClaimsPrincipal`. MethodCache warns about a cached implementation that takes those
  (MC0026).
- **Row-level security.** When the database filters rows by a setting on the connection, such as the current user,
  the same call returns different rows for different users. Make that setting an argument of the cached method, or
  don't cache the method.
- **Scoped services.** A scoped `DbContext` works as usual for a normal call. A refresh-ahead or fail-safe load can
  outlive the request, so it runs on a new instance from a new DI scope, with no `HttpContext`
  ([Refresh-ahead](features/expiration.md#refresh-ahead)). A service that sets up its connection per request must do
  the same from the method's arguments.

## Next steps

- Choose how long entries live: [Expiration](features/expiration.md).
- Remove entries when data changes: [Invalidation](features/invalidation.md).
- Change durations without a redeploy: [Configuration](features/configuration.md).
- Share the cache between instances: [Distributed cache](features/distributed-cache.md).
- Look up a task or a question: [How do I…?](how-to.md).
