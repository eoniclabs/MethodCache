[← MethodCache documentation](../README.md)

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
            async token => await orders.GetAsync(id, token),
            o => o.WithDuration(TimeSpan.FromMinutes(30)).AddTags("orders", $"order:{id}"),
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

`GetOrCreateAsync` takes every option, including a predicate that only makes sense per call:

<!-- snippet: manual-options -->
```cs
var list = await cache.GetOrCreateAsync(
    $"orders:{customerId}:{status}",
    async ct => await orders.ListAsync(customerId, status, ct),
    o => o.WithDuration(TimeSpan.FromMinutes(30))
          .WithSlidingExpiration(TimeSpan.FromMinutes(10))
          .WithRefreshAhead(TimeSpan.FromMinutes(5))
          .AddTags("orders", $"customer:{customerId}")
          .WithVersion(2)
          .WithEnabled(customerId > 0));   // skip the cache when false
```
<!-- endSnippet -->

- Duration, sliding expiration, refresh-ahead, tags and version work as described in [Expiration](expiration.md) and
  [Invalidation](invalidation.md).
- `WithEnabled(false)` runs the factory on every call, and neither reads nor stores the entry.
- `WithDistributed(false)` uses memory only. `CacheWhen<T>` checks the result before it is stored, and a value read
  from the distributed cache. `DurationFor<T>` gives each result its own duration in place of `WithDuration`
  ([A duration from the result](expiration.md#a-duration-from-the-result)).
- On a hot path, build the options once and reuse them: `static readonly CacheEntryOptions Options = new CacheEntryOptions.Builder().WithDuration(...).Build();`
  and pass `Options` instead of a builder action, so a call doesn't build options again.
- To avoid a closure, `ICacheManager.GetOrCreateAsync(key, state, (state, ct) => ..., options)` passes your state to a
  static factory.
- Configuration files and runtime overrides apply to declared cached methods. Explicit-key calls use their own options.
- `Build()` copies the tags, so changing the builder afterwards changes nothing.

`ICacheManager` has four operations: `GetOrCreateAsync`, `TryGetAsync`, `InvalidateKeysAsync` and
`InvalidateTagsAsync`. The interface takes every argument; extension methods give the short forms, such as
`TryGetAsync<T>(key, cancellationToken)`, `InvalidateKeyAsync(key)` and `InvalidateKeyAsync(key, version)`, and take a
builder action instead of built options. To write your own manager, see
[Registration](../reference/registration.md#your-own-cache-manager).

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

`TryGetAsync` takes the `version` an entry was stored with: `TryGetAsync<Order>("order:42", 2)`.

## Versions and the stored key

An entry stored with `WithVersion` lives under a different key than the unversioned one: `entry` at version 1 never
shares a value with the key `entry::v1`. `TryGetAsync` and `InvalidateKeyAsync` take the same key and version you
cached with: `TryGetAsync<Order>("order:42", 2)`, `InvalidateKeyAsync("order:42", 2)`.
`ICacheAdministrator` takes the stored key, `MethodCacheKeys.StoredKey(key, version)`, which is what
`CacheEntryInfo.StoredKey` shows; treat its format as opaque. To invalidate a versioned entry through `ICacheManager`,
pass the key and its version, `InvalidateKeyAsync(key, 2)`, not the stored key.

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
    async (missingKeys, ct) =>
    {
        var ids = missingKeys.Select(k => int.Parse(k["user:".Length..])).ToList();
        var loaded = await repository.GetUsersAsync(ids, ct);   // one query for all misses
        return loaded.ToDictionary(u => $"user:{u.Id}", u => u);
    },
    o => o.WithDuration(TimeSpan.FromHours(1)));
```
<!-- endSnippet -->

- The factory returns an `IReadOnlyDictionary` with a value for each key it gets, and the call returns one for every
  key. An answer that leaves out a key, or is `null`, fails like a factory that throws, and none of its values is
  cached.
- On a hot path, build the options once and pass the `CacheEntryOptions` instead of a builder action.
- Each key behaves as `GetOrCreateAsync` would for it. When the factory throws, each key it was asked for gets its
  fail-safe value; when a key has none, the factory's exception is thrown.
- `cancellationToken` stops this caller's wait. A factory that ignores it finishes in the background, and its values
  are cached unless they were invalidated while it ran.

## Things to know

- Concurrent calls for the same key share one factory run. A caller's `cancellationToken` stops its own wait; if it
  cancels a run others joined, they run the factory again rather than fail.
- A factory that asks for the key it is loading (itself, or through the cached service it belongs to) would wait for
  itself, so that call throws an `InvalidOperationException` instead, and the key is free for the next caller.
- To cache a sequence, materialize it in the factory (`await query.ToListAsync(ct)`) and cache the list.
- The four operations on `ICacheManager` can be mocked directly. The options-builder convenience overload
  delegates to that interface. You can also pass a `NoOpCacheManager` ([Testing](testing.md)).

- `TryGetAsync` returns a fresh entry, a cached `null` included, also when refresh-ahead is due; it never runs a
  factory or starts a refresh.
- An entry invalidated while `GetOrCreateManyAsync`'s factory runs is returned to that caller but not stored.
