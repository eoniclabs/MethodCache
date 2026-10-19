[← MethodCache documentation](../README.md)

# Conditional caching

Sometimes a result should be returned but not remembered: an empty search, a product that doesn't exist yet, a
response that says "try again later".

Chapter 9 of the [console demo](../../samples/MethodCache.Demo/) skips missing products and proves that a
newly created product is visible on the next lookup, while existing products are cached.

## CacheWhen

`CacheWhen` stores a result only when the predicate returns true:

<!-- snippet: conditional-cache-when -->
```cs
builder.Services.AddMethodCache(config =>
{
    // Don't remember empty search results: the next search asks the database again
    config.Method((IProductService s) => s.SearchAsync(Any<string>.Value))
          .CacheWhen(results => results.Count > 0);

    // A product that doesn't exist yet may be created any moment
    config.Method((IProductService s) => s.FindAsync(Any<int>.Value))
          .CacheWhen(product => product is not null);
});
```
<!-- endSnippet -->

- A rejected result is still returned to the caller, and the next call runs the method again.
- The entry it would have replaced, including a fail-safe value, stays.
- The predicate runs once per load, never on a cache hit. If it throws, the result is returned and not stored.
- It is set in startup code only, so it can't come from `appsettings.json`. `Any<T>.Value` stands for any argument.

> [!TIP]
> To stop caching `null` everywhere, set `CacheNullValues = false` on the [memory cache](memory-cache.md) instead.

## Skipping the cache for one call

With [manual caching](manual-caching.md), `WithEnabled` decides per call whether to use the cache at all:

<!-- snippet: conditional-when -->
```cs
var results = await cache.GetOrCreateAsync(
    $"search:{text}",
    async ct => await search(),
    o => o.WithDuration(TimeSpan.FromMinutes(10))
          .WithEnabled(!preview));   // preview requests skip the cache entirely
```
<!-- endSnippet -->

With `WithEnabled(false)`, the factory runs and its result is neither read from nor written to the cache.
To decide from the result instead, use `CacheWhen` ([Validation](configuration.md#validation)).
