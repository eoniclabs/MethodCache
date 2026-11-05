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
that nothing invalidates. It counts the `[CacheInvalidate]` methods of the registered services, the tags marked
`InvalidatedElsewhere` on `[Cache]` and the entity mapping of MethodCache.EntityFrameworkCore:

<!-- snippet: testing-tag-coverage -->
```cs
// Every tag of a cached method has a [CacheInvalidate] method, an EF Core mapping or InvalidatedElsewhere
Assert.Empty(TagCoverage.Uninvalidated(provider));
```
<!-- endSnippet -->

Name the tags your code invalidates in ways a container can't see, such as with `InvalidateTagAsync`:
`TagCoverage.Uninvalidated(provider, alsoInvalidatedBy: ["price:{provider}:{model}"])`. A placeholder matches any
value, so `"product:{id}"` covers `"product:{Id}"` and `"product:42"`.

Analyzer MC0028 checks the same within one project while you build; this check sees the whole application.

See [Testing](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/testing.md) for more.
