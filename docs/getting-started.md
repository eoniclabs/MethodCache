[← MethodCache documentation](README.md)

# Getting started

MethodCache 1.0 is a release candidate on nuget.org, so add `--prerelease` until 1.0.0 is out (the same goes for
the add-on packages):

```bash
dotnet add package MethodCache --prerelease
```

The `MethodCache` package contains the library, the source generator and the analyzers. It requires .NET 10 and the .NET 10 SDK. The same minimum applies to every add-on package.

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
lifetime you chose. Register cacheable services before this call. A later cacheable registration fails at host
startup, or when a generated decorator first binds without a host. Unrelated services may be registered later.
Calling `AddMethodCache` again decorates newly added services and keeps existing decorators intact.

Missing implementations are registered only with explicit `AutoRegister(ServiceLifetime.Scoped)` (or another
lifetime). Select the generated module with `FromAssemblyOf<ProductService>()`; exactly one accessible concrete
implementation must exist for each missing service. Zero or multiple candidates give an error naming the service,
module and candidates. Explicit DI registrations always retain their implementation and lifetime.

When multiple assemblies generate decorators for the same interface, select one module with
`FromAssemblyOf<SomeTypeInThatAssembly>()`. No entry-assembly or alphabetical preference is applied. The same
module selection and candidate data work under JIT and Native AOT.

Keyed registrations (`AddKeyedScoped<IProductService, ProductService>("primary")`) are cached too, under the same
key; an interface registered only keyed is not also registered without a key. The service key is part of the cache
keys, so each keyed registration of an interface caches its own results; it must be a string, enum, bool, char,
integer or `Guid` ([Keys](features/keys.md#things-to-know)). A catch-all registration with `KeyedService.AnyKey` is
rejected, since its implementation is created for whichever key is requested; register concrete keys instead.
A constructor parameter marked `[ServiceKey]` receives your key, as without MethodCache.

Several registrations of one interface, such as `AddScoped<IPriceProvider, StorePrices>()` and
`AddScoped<IPriceProvider, PartnerPrices>()` resolved as `IEnumerable<IPriceProvider>`, each cache their own results;
the implementation type is part of their keys. Two registrations of the same implementation type share their results
([Keys](features/keys.md#things-to-know)).
Open generic registrations and closed types without a generated binding also give a named startup error.

Generic interfaces work the same way: `IRepository<User>` is cached for every closed type a class in your project
implements, such as `UserRepository : IRepository<User>`. An open generic registration such as
`AddScoped(typeof(IRepository<>), typeof(Repository<>))` gives a named binding error at startup or first binding
without a host; register generated closed types instead.

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
  [MC0004](user-guide/DIAGNOSTICS.md#mc0004)). So is a lazy `IEnumerable<T>` or `IQueryable<T>`
  ([MC0015](user-guide/DIAGNOSTICS.md#mc0015)): return a list.
  Synchronous methods use L1/origin when the distributed adapter has no bounded native synchronous capability.
  Custom async-only managers bypass their synchronous caching path; see [Generated decorators](features/source-generator.md).
- Exceptions are never cached: the next call runs the method again.
- Concurrent calls with the same arguments share one call to your method (stampede protection). A caller that cancels
  only stops its own wait.
- Without a `Duration`, entries live five minutes (the built-in default) unless you set a
  default ([Expiration](features/expiration.md#duration)).
- `null` results are cached. Turn that off with `CacheNullValues` on the [memory cache](features/memory-cache.md), or
  per method with [conditional caching](features/conditional-caching.md).
- The cached object is returned as is, not copied. Treat cached results as read-only.

## Next steps

- Choose how long entries live: [Expiration](features/expiration.md).
- Remove entries when data changes: [Invalidation](features/invalidation.md).
- Change durations without a redeploy: [Configuration](features/configuration.md).
- Share the cache between instances: [Distributed cache](features/distributed-cache.md).
