[← MethodCache documentation](../README.md)

# Testing

The implementation of a cached interface is a plain class: create it directly in unit tests and no caching is
involved.

## Testing with caching

To test the caching itself, such as invalidation tags, build a service provider the way the application does:

<!-- snippet: testing-with-caching -->
```cs
var services = new ServiceCollection();
services.AddSingleton<Database>();
services.AddScoped<IProductService, ProductService>();
services.AddMethodCache();
var provider = services.BuildServiceProvider();

// Resolved through the generated decorator, exactly as in the application
var products = provider.CreateScope().ServiceProvider.GetRequiredService<IProductService>();
```
<!-- endSnippet -->

`ICacheAdministrator` from the same provider lists the entries and their keys, which helps when a test needs to check
what was cached ([Monitoring](monitoring.md)).

## Counting loads and invalidations

The `MethodCache.Testing` package records what the cache does. Add it to the test project at the same version as
`MethodCache`, and call `AddMethodCacheTesting()`: it registers a `CacheRecorder` and a `FakeTimeProvider` as the
container's clock.

<!-- snippet: testing-recorder -->
```cs
var services = new ServiceCollection();
services.AddSingleton<Database>();
services.AddScoped<IProductService, ProductService>();
services.AddMethodCache();
services.AddMethodCacheTesting();   // a fake clock and a recorder
await using var provider = services.BuildServiceProvider();
var recorder = provider.GetRequiredService<CacheRecorder>();
var time = provider.GetRequiredService<FakeTimeProvider>();
var products = provider.CreateScope().ServiceProvider.GetRequiredService<IProductService>();

await products.GetAsync(42);
await products.GetAsync(42);
Assert.Equal(1, recorder.Loads<IProductService>(p => p.GetAsync(42, default)));   // the second call was a hit

await products.UpdateAsync(42, new Product(42, "Green tea", 5m));
Assert.Contains("product:42", recorder.InvalidatedTags);
await products.GetAsync(42);
Assert.Equal(2, recorder.Loads<IProductService>(p => p.GetAsync(42, default)));

time.Advance(TimeSpan.FromMinutes(11));   // past the Duration of 10 minutes
await products.GetAsync(42);
Assert.Equal(3, recorder.Loads<IProductService>(p => p.GetAsync(42, default)));
```
<!-- endSnippet -->

`Loads` counts how many times one call, with these arguments, ran your implementation: its misses, refreshes and
reloads after an invalidation. `InvalidatedTags` and `InvalidatedKeys` list what was invalidated, in order, by
`[CacheInvalidate]` methods, `ICacheManager` calls and `MethodCache.EntityFrameworkCore`. `Clear()` forgets what the
test's setup recorded. The recorder sees the built-in cache manager only: with `NoOpCacheManager` or your own
`ICacheManager`, it records nothing.

## When a test writes to the database directly

A test that changes data without going through a `[CacheInvalidate]` method, such as seeding rows or calling a
repository, leaves the cached results as they were. Clear the cache after the write, or invalidate what it changed:

<!-- snippet: testing-direct-write -->
```cs
await database.SaveAsync(changed);   // the test changes the data behind the cache's back

// Remove everything cached...
await provider.GetRequiredService<ICacheAdministrator>().ClearAsync();

// ...or only what the write changed
var cache = provider.GetRequiredService<ICacheManager>();
var keys = provider.GetRequiredService<IMethodCacheKeys>();
await cache.InvalidateTagAsync(keys.Tag<IProductService>(p => p.GetAsync(changed.Id, default), "product:{id}"));
```
<!-- endSnippet -->

`ClearAsync` also removes the entries from a distributed cache and the other instances, so use a cache of the test's
own (a new service provider, or its own `KeyPrefix`) when tests run in parallel against a shared Redis or database.

## Proving that a result is cached

A test that changes a row and reads it again proves caching only when the change doesn't invalidate. With
`MethodCache.EntityFrameworkCore`, a save through the connected context invalidates, so the second read is fresh
whether or not it is cached. Count the runs of your implementation with `recorder.Loads` instead, as above. To test
the stale value itself, change the row past MethodCache: with SQL, or through a context type that isn't connected to
MethodCache, over the same database. With EF Core's in-memory provider, give both contexts the same
`InMemoryDatabaseRoot`.

## Every tag has an invalidation

A tag that nothing invalidates keeps its entries until they expire. `TagCoverage.Uninvalidated` lists the tags of the
cached methods, from attributes, startup code and configuration, that no `[CacheInvalidate]` method of a registered
service invalidates. Name the tags your code invalidates in other ways, such as with `InvalidateTagAsync` or in
another application:

<!-- snippet: testing-tag-coverage -->
```cs
// Every tag of a cached method has a [CacheInvalidate] method, or is in the list
Assert.Empty(TagCoverage.Uninvalidated(provider, alsoInvalidatedBy: ["food-catalog"]));
```
<!-- endSnippet -->

A placeholder matches any value, so `"product:{id}"` covers `"product:{Id}"` and `"product:42"`. With
`MethodCache.EntityFrameworkCore`, pass the tags of the mapping:

<!-- snippet: efcore-tag-coverage -->
```cs
var mapping = provider.GetRequiredService<EntityTagMapping<ShopDb>>();
// [(Product, ["product:{Id}", "category:{CategoryId}"], []), (OrderLine, ["top-sellers"], [])]
var entities = mapping.Entities;

// Every tag of a cached method is mapped, or has a [CacheInvalidate] method
Assert.Empty(TagCoverage.Uninvalidated(provider, alsoInvalidatedBy: mapping.AllTags));
```
<!-- endSnippet -->

Analyzer [MC0028](../user-guide/DIAGNOSTICS.md#mc0028) checks the same within one project while you build; this
check sees the whole application, including the tags in configuration.

## Turning method caching off

Register `NoOpCacheManager` after `AddMethodCache()` and every call reaches your implementation:

<!-- snippet: testing-without-caching -->
```cs
var services = new ServiceCollection();
services.AddSingleton<Database>();
services.AddScoped<IProductService, ProductService>();
services.AddMethodCache();
services.AddSingleton<ICacheManager, NoOpCacheManager>();   // after AddMethodCache: every call reaches ProductService
```
<!-- endSnippet -->

For code that uses [manual caching](manual-caching.md), pass a `NoOpCacheManager` directly, or mock the four
operations on `ICacheManager` (the short forms are extension methods that call them):

<!-- snippet: manual-testing -->
```cs
ICacheManager cache = new NoOpCacheManager();   // never caches: the factory runs on every call
var reader = new OrderReader(cache, repository);
```
<!-- endSnippet -->

`NoOpCacheManager` is in the `MethodCache` namespace. It turns off method caching only; responses of an `HttpClient`
with `AddHttpCaching` are still cached.

## Time

> [!TIP]
> The cache reads the time from the `TimeProvider` registered in the container, if there is one. Register a fake
> `TimeProvider`, such as the `FakeTimeProvider` that `AddMethodCacheTesting()` registers, to test expiration without
> waiting.
> This includes the background cleanup of the memory cache and, with `MethodCache.HttpCaching`, response freshness,
> eviction and the background revalidation timeout.

