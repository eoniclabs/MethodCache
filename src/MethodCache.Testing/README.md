# MethodCache.Testing

Helpers for tests of the caching itself: a fake clock, a recorder that counts how often your implementation ran and
what was invalidated, and a check that every cache tag has something that invalidates it.

Install it in the test project, at the same version as `MethodCache`.

```bash
dotnet add package MethodCache.Testing
```

## Recorder and fake clock

`AddMethodCacheTesting()` registers a `FakeTimeProvider` as the container's `TimeProvider` and a `CacheRecorder`.
Build the provider as the application does and resolve both:

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

`Loads` counts the runs of one call, with its arguments; a hit doesn't count. `InvalidatedTags` and
`InvalidatedKeys` list what `[CacheInvalidate]` methods, `ICacheManager` calls and MethodCache.EntityFrameworkCore
invalidated, in order. `Clear()` forgets what a test's setup recorded. The clock starts at 2000-01-01 and only moves
when the test advances it, so nothing expires on its own.

The recorder sees the built-in cache manager: with `NoOpCacheManager` or your own `ICacheManager` it records nothing.

## Every tag has an invalidation

`TagCoverage.Uninvalidated` lists the tags of the cached methods, from attributes, startup code and configuration,
that no `[CacheInvalidate]` method of a registered service invalidates. Name the tags your code invalidates in other
ways, such as with `InvalidateTagAsync`:

<!-- snippet: testing-tag-coverage -->
```cs
// Every tag of a cached method has a [CacheInvalidate] method, or is in the list
Assert.Empty(TagCoverage.Uninvalidated(provider, alsoInvalidatedBy: ["food-catalog"]));
```
<!-- endSnippet -->

A placeholder matches any value, so `"product:{id}"` covers `"product:{Id}"` and `"product:42"`. With
MethodCache.EntityFrameworkCore, pass the mapping's tags:

<!-- snippet: efcore-tag-coverage -->
```cs
var mapping = provider.GetRequiredService<EntityTagMapping<ShopDb>>();
// [(Product, ["product:{Id}", "category:{CategoryId}"], []), (OrderLine, ["top-sellers"], [])]
var entities = mapping.Entities;

// Every tag of a cached method is mapped, or has a [CacheInvalidate] method
Assert.Empty(TagCoverage.Uninvalidated(provider, alsoInvalidatedBy: mapping.AllTags));
```
<!-- endSnippet -->

Analyzer MC0028 checks the same within one project while you build; this check sees the whole application.

See [Testing](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/testing.md) for more.
