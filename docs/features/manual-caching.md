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
          .WithEnabled(customerId > 0));   // skip the cache when false
```
<!-- endSnippet -->

- Duration, sliding expiration, refresh-ahead, tags and version work as described in [Expiration](expiration.md) and
  [Invalidation](invalidation.md).
- `WithEnabled(false)` runs the factory for each call and bypasses reads, joins, stale values and publication.
- `WithDistributed(false)` uses memory only. `CacheWhen<T>` checks the result before caching and after L2 reads.
- `CacheContext` carries the logical key and an `Items` dictionary for the factory invocation.
- Configuration files and runtime overrides apply to declared cached methods. Explicit-key calls use their own options.
- `Build()` copies mutable collections. Later builder changes cannot change published options.

`ICacheManager` has four operations: `GetOrCreateAsync`, `TryGetAsync`, `InvalidateKeysAsync` and
`InvalidateTagsAsync`. The interface accepts immutable options; the helpers accept builder actions or simpler factories.
A custom manager uses `options.GetCacheKey(key)` to apply versions once and `options.CanCache(value)` to evaluate the
result predicate. Its lookup and key-invalidation methods normalize their logical keys with
`CacheManagerExtensions.GetCacheKey(key, version)`, using the optional version unchanged when forwarding calls.
Generated calls already carry complete keys; the same helper handles them without adding another version.
Custom managers own their caching semantics and must preserve presence separately from null/default values. Generated
synchronous methods use the underlying implementation directly when a custom manager has no internal sync capability.

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
always holds the key you passed. `TryGetAsync` and `InvalidateKeysAsync` accept that same logical key and optional
version; they apply the same framing as storage. `InvalidateKeyAsync(key, version)` is a convenience for one logical key.
`ICacheAdministrator` accepts the physical stored key from `CacheManagerExtensions.GetCacheKey(key, version)`;
do not construct that internal format yourself.

## Many keys at once

Key, tag and clear invalidation also guard the batch factory while it reads its source. Its caller can receive the in-flight result, but invalidated candidates are not published into memory or the distributed cache. Individual callers can still load and publish these keys during the batch; a fresh concurrent value wins.

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
- A factory that asks for the key it is loading (itself, or through the cached service it belongs to) would wait for
  itself, so that call throws an `InvalidOperationException` instead, and the key is free for the next caller.
- To cache a sequence, materialize it in the factory (`await query.ToListAsync(ct)`) and cache the list.
- The four operations on `ICacheManager` can be mocked directly. The options-builder convenience overload
  delegates to that interface. You can also pass a `NoOpCacheManager` ([Testing](testing.md)).

Manual `TryGetAsync` reads a fresh entry, including a cached null, even when refresh-ahead is due. This lookup does not start a factory or background refresh.

Caller cancellation stops the wait during an L2 read or a slow factory, including bulk reads and fail-safe waits. Shared work may finish for other callers; cancellation is not reported as a dependency failure.
