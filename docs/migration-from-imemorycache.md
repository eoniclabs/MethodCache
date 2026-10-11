# Migrating from IMemoryCache

With `IMemoryCache`, every cached method holds the same pattern: build a key, try the cache, load, store, and remember
every key to remove when data changes. MethodCache moves that into an attribute on the interface.

<table>
<tr><th>Before: IMemoryCache</th><th>After: MethodCache</th></tr>
<tr><td>

<!-- snippet: readme-before -->
```cs
public async Task<User> GetUserAsync(int id)
{
    var key = $"user:{id}";
    if (_cache.TryGetValue(key, out User? user))
        return user!;

    // Two callers can both miss and both hit the database
    user = await _db.LoadUserAsync(id);
    _cache.Set(key, user, TimeSpan.FromMinutes(10));
    return user;
}

public async Task RenameAsync(int id, string name)
{
    await _db.RenameAsync(id, name);
    _cache.Remove($"user:{id}");
    // ...and every other key that held this user
}
```
<!-- endSnippet -->

</td><td>

<!-- snippet: readme-after -->
```cs
[Cache(Duration = "00:10:00", Tags = ["user:{id}"])]
Task<User> GetUserAsync(int id);

[CacheInvalidate(Tags = ["user:{id}"])]
Task RenameAsync(int id, string name);
```
<!-- endSnippet -->

</td></tr>
</table>

## Steps

1. Add the `MethodCache` package and call `builder.Services.AddMethodCache()` after your registrations
   ([Getting started](getting-started.md)).
2. If the class has no interface, extract one. Callers must use the interface, because the cache is a decorator around
   it.
3. Move the caching settings to `[Cache]` on the interface method, and delete the cache code from the implementation.
4. Replace each `Remove(key)` with `[CacheInvalidate(Tags = ...)]` on the method that changes the data, and give the
   cached methods matching tags ([Invalidation](features/invalidation.md)).
5. Code that can't move to an attribute, such as a cached call in the middle of a method, uses
   `ICacheManager.GetOrCreateAsync` with the key it already has ([Manual caching](features/manual-caching.md)).

Migrate one service at a time; both caches can run side by side.

## What maps to what

| IMemoryCache | MethodCache |
|--------------|-------------|
| `TryGetValue` + `Set` | `[Cache]`, or `GetOrCreateAsync` with your key |
| `SetAbsoluteExpiration` | `Duration` |
| `SetSlidingExpiration` | `SlidingExpiration`, capped by `Duration` |
| `Remove(key)` | `[CacheInvalidate]` with tags, or `InvalidateByKeysAsync` |
| `Size` and `SizeLimit` | `Weigher` and `MaxMemoryBytes` ([Memory cache](features/memory-cache.md)) |
| `Priority` | Not needed: W-TinyLFU keeps the entries that are read often |
| `GetOrCreateAsync`, which may run the factory several times at once | Concurrent misses share one call |
| A separate `IDistributedCache` | `AddMethodCacheRedis(...)`, with the memory cache kept in sync ([Distributed cache](features/distributed-cache.md)) |

## Differences to know

- **Keys are generated** from the method and its arguments ([Cache keys](features/keys.md)). Check that every argument
  that changes the result is a parameter, not a field of the class.
- **`null` is cached** by default. Set `CacheNullValues = false` if your old code skipped `null`.
- **Exceptions are never cached**, as before.
