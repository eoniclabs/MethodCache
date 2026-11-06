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

The key is used as it is: MethodCache adds neither the result type nor the application to it. Applications that share
a distributed cache under one `KeyPrefix`, as they do to invalidate each other's entries, therefore share these keys,
and `order:42` in two of them is one entry. Cache the same type under a key everywhere, or start it with the
application's name, such as `billing:order:42`. Keys of `[Cache]` methods include the method's assembly and interface,
so they don't collide this way ([Several applications on one store](distributed-cache.md#several-applications-on-one-store)).

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
  ([A duration from the result](expiration.md#a-duration-from-the-result)). `TagsFor<T>` adds tags read from the
  result ([Tags from the result](invalidation.md#tags-from-the-result)).
- On a hot path, build the options once and reuse them: `static readonly CacheEntryOptions Options = new CacheEntryOptions.Builder().WithDuration(...).Build();`
  and pass `Options` instead of a builder action, so a call doesn't build options again.
- To avoid a closure, `ICacheManager.GetOrCreateAsync(key, state, (state, ct) => ..., options)` passes your state to a
  static factory.
- Configuration files and runtime overrides apply to declared cached methods. Explicit-key calls use their own options.
- `Build()` copies the tags, so changing the builder afterwards changes nothing.

`ICacheManager` has four operations: `GetOrCreateAsync`, `TryGetAsync`, `InvalidateKeysAsync` and
`InvalidateTagsAsync`. The interface takes every argument; extension methods give the short forms, such as
`TryGetAsync<T>(key, cancellationToken)`, `InvalidateKeyAsync(key)` and `InvalidateKeyAsync(key, version)`, and take a
builder action instead of built options. `SetAsync` is an extension method too: on a manager of your own it invalidates
the key and then stores the value through `GetOrCreateAsync`. To write your own manager, see
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

## Storing a value you already have

When you already have the new value, such as right after you saved it, `SetAsync` stores it so the next read doesn't
load it again:

<!-- snippet: manual-set -->
```cs
var saved = await orders.SaveAsync(order, ct);
await cache.SetAsync($"order:{saved.Id}", saved,
    o => o.WithDuration(TimeSpan.FromMinutes(30)).AddTags("orders", $"order:{saved.Id}"),
    ct);
```
<!-- endSnippet -->

- It stores the value in memory and the distributed cache, and other instances drop their copy, so their next read
  gets the new value from the distributed cache. With `WithDistributed(false)` it stays in this instance's memory, and
  the copies in the distributed cache and on other instances are removed.
- It takes the options `GetOrCreateAsync` takes. Use the same duration, tags and version as the calls that read the key,
  since the value replaces theirs. `DurationFor<T>` gives it its own lifetime.
- A value the options turn down isn't stored, and the key's old value is removed instead: one `CacheWhen<T>` rejects,
  one `DurationFor<T>` gives no time, or `null` without `CacheNullValues`. `WithEnabled(false)` does nothing.
- A load of the key that is running when you call it doesn't overwrite the value with what it read before.
- To remove a value instead, invalidate the key or its tags ([Invalidation](invalidation.md)).

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
the writer's value, as an individual read would.

Pass the ids and how each id makes its key. The factory gets the ids that are not cached and returns a dictionary by
id, and so does the call:

<!-- snippet: manual-many -->
```cs
var users = await cache.GetOrCreateManyAsync<int, User>(
    userIds,
    id => $"user:{id}",
    async (missingIds, ct) =>
    {
        var loaded = await repository.GetUsersAsync(missingIds, ct);   // one query for all misses
        return loaded.ToDictionary(u => u.Id);   // an id without a user is left out
    },
    o => o.WithDuration(TimeSpan.FromHours(1)));
```
<!-- endSnippet -->

- An id the factory leaves out, such as one that doesn't exist, is not cached and is left out of the returned
  dictionary. When the key has an expired fail-safe value, it gets that value instead, as when the factory throws. To
  cache that an id has no value, return it with `null`.
- Two different ids that make the same key throw an `ArgumentException`, since one would get the other's value.
- The factory returns a dictionary, an empty one when no id has a value; `null` fails like a factory that throws.
- On a hot path, build the options once and pass the `CacheEntryOptions` instead of a builder action.
- Each key behaves as `GetOrCreateAsync` would for it. When the factory throws, each key it was asked for gets its
  fail-safe value; when a key has none, the factory's exception is thrown.
- `cancellationToken` stops this caller's wait. A factory that ignores it finishes in the background, and its values
  are cached unless they were invalidated while it ran.

With string keys, pass the keys; the factory gets the keys that are not cached and returns a dictionary by key, with
the same rules:

<!-- snippet: manual-many-keys -->
```cs
var users = await cache.GetOrCreateManyAsync<User>(
    userIds.Select(id => $"user:{id}"),
    async (missingKeys, ct) =>
    {
        var ids = missingKeys.Select(k => int.Parse(k["user:".Length..])).ToList();
        var loaded = await repository.GetUsersAsync(ids, ct);
        return loaded.ToDictionary(u => $"user:{u.Id}");
    },
    o => o.WithDuration(TimeSpan.FromHours(1)));
```
<!-- endSnippet -->

## Things to know

- Concurrent calls for the same key share one factory run, which gets the token of the caller that started it. A
  caller's `cancellationToken` stops its own wait; if it cancels a run others joined, they run the factory again rather
  than fail.
- Name the factory's token and pass it on, as in `async ct => await orders.GetAsync(id, ct)`. CA2016 asks for that when
  the factory calls a method that takes a token, and named `ct` it hides the method's own `ct` inside the factory. Pass
  the method's token as `cancellationToken`: a token from outside the factory that the call doesn't pass, such as
  `HttpContext.RequestAborted`, fails the shared run for every caller when it is cancelled.
  [MC0035](../user-guide/DIAGNOSTICS.md#mc0035) reports it, with a code fix.
- A factory that asks for the key it is loading (itself, or through the cached service it belongs to) would wait for
  itself, so that call throws an `InvalidOperationException` instead, and the key is free for the next caller.
- To cache a sequence, materialize it in the factory (`await query.ToListAsync(ct)`) and cache the list.
- The four operations on `ICacheManager` can be mocked directly. The options-builder convenience overload
  delegates to that interface. You can also pass a `NoOpCacheManager` ([Testing](testing.md)).

- `TryGetAsync` returns a fresh entry, a cached `null` included, also when refresh-ahead is due; it never runs a
  factory or starts a refresh.
- An entry invalidated while `GetOrCreateManyAsync`'s factory runs is returned to that caller but not stored.
