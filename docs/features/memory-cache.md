# Memory cache

The in-memory cache uses W-TinyLFU eviction by default: when it is full, it keeps the entries that are read often and
evicts the rest, so a burst of one-off reads doesn't push out your hot data. Configure it with
`MethodCacheMemoryOptions`:

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
| `MaxMemoryBytes` | 1000 MB | The most total weight kept; used only with a `Weigher` |
| `Weigher` | none | Returns an entry's cost, for example its size in bytes |
| `EvictionPolicy` | `TinyLfu` | What to evict when full: `TinyLfu`, `Lru` (least recently read) or `Fifo` (oldest stored) |
| `DefaultExpiration` | 5 minutes | The duration of methods with none of their own and no `DefaultPolicy` |
| `MaxExpiration` | 7 days | No entry lives longer, whatever its duration |
| `JitterMaxDuration` | zero | Adds a random extra lifetime up to this value, so entries created together don't expire together |
| `CacheNullValues` | `true` | Caches `null` results like any other value |
| `EnableStatistics` | `true` | Counts hits, misses and evictions for the [statistics](monitoring.md) |
| `EnableFastPath` | `true` | Generated decorators answer hits from a lookup that allocates nothing; turning it off only makes hits slower |
| `EnableBackgroundCleanup` | `true` | Removes expired entries on a timer, not only when they are read |
| `CleanupInterval` | 1 minute | How often the background cleanup runs; 10 seconds to 24 hours |
| `MemoryCalculationMode` | `Fast` | How `MemoryUsage` in the statistics is estimated; see [Memory statistics](#memory-statistics) |

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

## Memory statistics

With a `Weigher`, `GetStatsAsync().MemoryUsage` is the total weight of the entries, the same number that is checked
against `MaxMemoryBytes`. Without one, it is a rough estimate, chosen with `MemoryCalculationMode`:

<!-- snippet: memory-statistics -->
```cs
builder.Services.Configure<MethodCacheMemoryOptions>(o =>
{
    o.MemoryCalculationMode = MemoryUsageCalculationMode.Sampling;   // default Fast
    o.SamplingPercentage = 0.05;                                     // default 0.1 (10% of entries)
});
```
<!-- endSnippet -->

| Mode | How `MemoryUsage` is estimated |
|------|--------------------------------|
| `Fast` (default) | From the entry count and the types and lengths of a sample of the values (strings, arrays, collections) |
| `Sampling` | Serializes a sample of the values to JSON and scales the average size by the entry count |
| `Accurate` | Serializes every value to JSON. Despite the name, this is an estimate too |
| `Disabled` | Always 0 |

None of the modes measures the real memory of object graphs, shared references or the GC heap; use the number to see
trends. The sampling options:

- `SamplingPercentage` (default 0.1) is the share of entries in one sample, at most `SamplingMaxEntries` (default
  1024).
- `SamplingRecalculationInterval` (default 100) is the number of statistics reads before a new sample is taken; 1
  samples on every read. In between, the previous average is scaled by the current entry count.
- `AccurateModeRecalculationInterval` (default 1000) is the number of statistics reads between full calculations in
  `Accurate` mode; reads in between get a `Fast` estimate.

## How strict the bound is

Cache hits never wait for eviction. It runs in batches off the read path, so a new entry is stored at once and counted
against `MaxItems` (or `MaxMemoryBytes`) when the next batch applies it. Under any load:

- The counted size or weight is never more than one entry over the bound.
- When writers get ahead of eviction, they wait for it and help with it, so at most `16 × processor count` (at least
  64) inserts, plus two per writing thread, are stored but not yet counted.

Moments after the writes stop, the cache is back within the bound.

## Null values

`null` results are cached by default, so a method that returns `null` isn't run again on every call. Set
`CacheNullValues = false` when `null` means "not there yet" and the next call should try again, or use
[`CacheWhen`](conditional-caching.md) for one method.

## Cached objects are shared

The cache stores the object your method returned and hands the same instance to every caller. Don't change a cached
object; return immutable types such as records, or copies.

Weighted capacity accounting remains nonnegative even for valid weights near `long.MaxValue`. Before an addition could overflow, the engine evicts existing weight; normal capacity/admission behavior is unchanged below that arithmetic limit.
