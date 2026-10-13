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
- `OnHit`, `OnMiss` and `WithMetrics` observe this call. The `CacheContext` they get carries the key, an `Items`
  dictionary scoped to the call and `Services`, the `services` argument of `GetOrCreateAsync` (null when you don't
  pass one).
- These policies live in code: `appsettings.json` and runtime overrides apply to `[Cache]` methods only. `When`,
  `OnHit`, `OnMiss` and `WithMetrics` work here only ([Validation](configuration.md#validation)).
- `CacheEntryOptions.Builder.Build()` takes a copy of the tags and callbacks, so changing the builder afterwards
  doesn't change options already built.

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

## Versions and the stored key

An entry stored with `WithVersion` lives under a different key than the unversioned one: `entry` at version 1 never
shares a value with the key `entry::v1`. Keys that begin with a NUL character are escaped too. `CacheContext.Key`
always holds the key you passed. To invalidate or look up such an entry directly, for example with
`InvalidateByKeysAsync` or `ICacheAdministrator`, get the stored key from `CacheManagerExtensions.GetCacheKey(key,
version)` (leave out `version` for an unversioned entry) instead of building it by hand.

## Many keys at once

`GetOrCreateManyAsync` checks the local cache for every key, looks up the local misses in the distributed cache,
and calls the factory once with the keys neither holds. Each key gets the value the cache selects: memory first, then
the distributed cache, then the factory. A bulk read therefore agrees with individual reads of the same keys, even
across nodes: a value another node stored in the distributed cache is returned as is, and its key is not passed to
the factory. A concurrent writer may still win over a value the factory produced; the returned dictionary then holds
the writer's value, as an individual read would:

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

Manual `TryGetAsync` reads a fresh entry, including a cached null, even when the fast path is disabled or refresh-ahead is due. This lookup does not start a factory or background refresh.

An explicit `SetAsync` with a lifetime shorter than L2 millisecond precision removes the previous distributed value instead of leaving it readable. If the backend is unavailable, the removal follows the normal invalidation retry path.

Caller cancellation stops the wait during an L2 read or a slow factory, including bulk reads and fail-safe waits. Shared work may finish for other callers; cancellation is not reported as a dependency failure.
