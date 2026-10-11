# Memory cache

The in-memory cache is a W-TinyLFU cache: when it is full, it keeps the entries that are read often and evicts the
rest, so a burst of one-off reads doesn't push out your hot data. Configure it with `MethodCacheMemoryOptions`:

<!-- snippet: memory-options -->
```cs
builder.Services.Configure<MethodCacheMemoryOptions>(o =>
{
    o.MaxItems = 50_000;                              // default 10,000
    o.DefaultExpiration = TimeSpan.FromMinutes(10);   // for methods without a Duration; default 5 minutes
    o.JitterMaxDuration = TimeSpan.FromSeconds(30);   // spread out entries created together
    o.CacheNullValues = false;                        // a null result means "try again next time"
});
```
<!-- endSnippet -->

| Option | Default | What it does |
|--------|---------|--------------|
| `MaxItems` | 10,000 | The most entries kept |
| `MaxMemoryBytes` | | The most total weight kept when a `Weigher` is set |
| `Weigher` | none | Returns an entry's cost, for example its size in bytes |
| `DefaultExpiration` | 5 minutes | The duration of methods with none of their own and no `DefaultPolicy` |
| `MaxExpiration` | 7 days | No entry lives longer, whatever its duration |
| `JitterMaxDuration` | zero | Adds a random extra lifetime up to this value, so entries created together don't expire together |
| `CacheNullValues` | `true` | Caches `null` results like any other value |
| `EnableStatistics` | `true` | Counts hits, misses and evictions for the [statistics](monitoring.md) |

## Bounding by size

Object sizes can't be measured reliably at runtime, so to bound the cache by memory you say what an entry costs:

<!-- snippet: memory-weigher -->
```cs
builder.Services.Configure<MethodCacheMemoryOptions>(o =>
{
    o.MaxMemoryBytes = 256 * 1024 * 1024;   // bound the cache by size instead of by count
    o.Weigher = (key, value) => value is string s ? s.Length * 2 : 64;
});
```
<!-- endSnippet -->

The weigher must be fast, thread-safe and return a non-negative value. Without one, `MaxMemoryBytes` is ignored.

## Null values

`null` results are cached by default, so a method that returns `null` isn't run again on every call. Set
`CacheNullValues = false` when `null` means "not there yet" and the next call should try again, or use
[`CacheWhen`](conditional-caching.md) for one method.

## Cached objects are shared

The cache stores the object your method returned and hands the same instance to every caller. Don't change a cached
object; return immutable types such as records, or copies.
