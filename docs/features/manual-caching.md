# Manual caching

When an attribute doesn't fit, such as a call in the middle of a method or a client without an interface, use the same
cache through `ICacheManager` with a key you choose:

<!-- snippet: manual-basic -->
```cs
public sealed class OrderReader(ICacheManager cache, IOrderRepository orders)
{
    public ValueTask<Order> GetAsync(int id, CancellationToken ct = default) =>
        cache.GetOrCreateAsync(
            $"order:{id}",
            async (ctx, token) => await orders.GetAsync(id, token),
            o => o.WithDuration(TimeSpan.FromMinutes(30)).WithTags("orders", $"order:{id}"),
            cancellationToken: ct);
}
```
<!-- endSnippet -->

It shares the in-memory cache, the distributed cache, stampede protection and invalidation with `[Cache]` methods.

## Keys

You always pass the key yourself. MethodCache doesn't derive keys from the lambda, so a captured variable never ends
up in a key by accident, and two calls share an entry only when their keys are equal. Use a prefix per kind of data,
such as `order:42`, so keys from different places can't collide. An empty or whitespace key throws
`ArgumentException`.

## Options

`GetOrCreateAsync` takes every option, including callbacks and a predicate that only make sense per call:

<!-- snippet: manual-options -->
```cs
var list = await cache.GetOrCreateAsync(
    $"orders:{customerId}:{status}",
    async (ctx, ct) => await orders.ListAsync(customerId, status, ct),
    o => o.WithDuration(TimeSpan.FromMinutes(30))
          .WithSlidingExpiration(TimeSpan.FromMinutes(10))
          .RefreshAhead(TimeSpan.FromMinutes(5))
          .WithTags("orders", $"customer:{customerId}")
          .WithVersion(2)
          .OnHit(ctx => logger.LogDebug("Cache hit: {Key}", ctx.Key))
          .OnMiss(ctx => logger.LogDebug("Cache miss: {Key}", ctx.Key))
          .When(ctx => customerId > 0));   // skip the cache when false
```
<!-- endSnippet -->

- Duration, sliding expiration, refresh-ahead, tags and version work as described in [Expiration](expiration.md) and
  [Invalidation](invalidation.md).
- `When` skips the cache for this call when it returns false ([Conditional caching](conditional-caching.md)).
- `OnHit`, `OnMiss` and `WithMetrics` observe this call. The `CacheContext` they get carries the key and an `Items`
  dictionary scoped to the call.
- These policies live in code: `appsettings.json` and runtime overrides apply to `[Cache]` methods only.

## Reading without loading

<!-- snippet: manual-try-get -->
```cs
var lookup = await cache.TryGetAsync<Order>("order:42");
if (lookup.Found)
{
    Console.WriteLine(lookup.Value);
}
```
<!-- endSnippet -->

`TryGetAsync` takes the same `version` an entry was stored with.

## Many keys at once

`GetOrCreateManyAsync` looks up every key and calls the factory once, with only the keys that were missing:

<!-- snippet: manual-many -->
```cs
var users = await cache.GetOrCreateManyAsync<User>(
    userIds.Select(id => $"user:{id}"),
    async (missingKeys, ctx, ct) =>
    {
        var ids = missingKeys.Select(k => int.Parse(k["user:".Length..])).ToList();
        var loaded = await repository.GetUsersAsync(ids, ct);   // one query for all misses
        return loaded.ToDictionary(u => $"user:{u.Id}", u => u);
    },
    o => o.WithDuration(TimeSpan.FromHours(1)));
```
<!-- endSnippet -->

## Things to know

- Concurrent calls for the same key share one factory run. A caller's `cancellationToken` stops its own wait; if it
  cancels a run others joined, they run the factory again rather than fail.
- To cache a sequence, materialize it in the factory (`await query.ToListAsync(ct)`) and cache the list.
- `GetOrCreateAsync` is an extension method, so mocking libraries can't intercept it. In tests, pass a
  `NoOpCacheManager` ([Testing](testing.md)).
