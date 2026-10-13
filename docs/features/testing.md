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

## Turning caching off

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

For code that uses [manual caching](manual-caching.md), pass a `NoOpCacheManager` directly. `GetOrCreateAsync` is an
extension method, so mocking libraries can't intercept it:

<!-- snippet: manual-testing -->
```cs
ICacheManager cache = new NoOpCacheManager();   // never caches: the factory runs on every call
var reader = new OrderReader(cache, repository);
```
<!-- endSnippet -->

`NoOpCacheManager` is in `MethodCache.Extensibility`.

## Time

The cache reads the time from the `TimeProvider` registered in the container, if there is one. Register a fake
`TimeProvider`, for example from `Microsoft.Extensions.TimeProvider.Testing`, to test expiration without waiting.
This includes the background cleanup of the memory cache and, with `MethodCache.HttpCaching`, response freshness,
eviction and the background revalidation timeout.

## Behavior and compatibility

## Testing your service

The web demo uses a shared SQLite database in the temp folder (`methodcache-webdemo-products.db`), so concurrent price updates from either demo instance are atomic. Decimal prices retain full precision. The earlier temporary JSON file is left untouched; the SQLite catalog starts with the demo seed data.
