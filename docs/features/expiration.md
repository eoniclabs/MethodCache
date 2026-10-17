# Expiration

Each cached method decides how long its entries live. The settings below can be set on `[Cache]`, in startup code, in
`appsettings.json` or at runtime ([Configuration](configuration.md)).

<!-- snippet: expiration-attributes -->
```cs
public interface IReportService
{
    // Gone ten minutes after it was stored
    [Cache(Duration = "00:10:00")]
    Task<Report> GetAsync(int year);

    // Kept while it is read at least every five minutes, but never longer than an hour
    [Cache(Duration = "01:00:00", SlidingExpiration = "00:05:00")]
    Task<Session> GetSessionAsync(string id);

    // Reloaded in the background when it is read in its last minute, so callers never wait
    [Cache(Duration = "00:10:00", RefreshAhead = "00:01:00")]
    Task<PriceList> GetPricesAsync(string region);

    // Changing the version changes every key, so values cached by the previous version are ignored
    [Cache(Duration = "1.00:00:00", Version = 2)]
    Task<Report> GetYearlyAsync(int year);
}
```
<!-- endSnippet -->

Durations accept an invariant positive decimal followed by `s`, `m`, `h` or `d` (case-insensitive), or `[d.]hh:mm:ss` with up to seven fractional second digits. Examples: `"30s"`, `"1.5h"`, `"1.00:00:00"`. Outer whitespace is ignored. The result must be an exact positive whole number of ticks; fractional ticks are rejected. Bare integers, zero, negatives, exponents, decimal commas, combined units and overflow are errors. The same parser is used for attributes and configuration; invalid explicit attributes produce [MC0004](../user-guide/DIAGNOSTICS.md#mc0004) even without analyzers. `FailSafe` also accepts `"off"`; builder `FailSafe(TimeSpan.Zero)` disables it. Timeout and retry windows must stay positive.

## Duration

`Duration` is how long an entry lives after it was stored. A method without one uses the global default:

<!-- snippet: expiration-default -->
```cs
builder.Services.AddMethodCache(config =>
    config.Defaults(o => o.WithDuration(TimeSpan.FromMinutes(15))));
```
<!-- endSnippet -->

Without any default, entries live for five minutes. No entry lives
longer than `MethodCacheMemoryOptions.MaxExpiration` (seven days). See [Memory cache](memory-cache.md).

## Sliding expiration

`SlidingExpiration` keeps an entry while it is read: each read extends its life by the sliding window. It never lives
past its `Duration`; without a `Duration`, only `MaxExpiration` limits it.

## Refresh-ahead

`RefreshAhead` reloads an entry in the background when it is read within that window before it expires. The caller
gets the cached value at once, and the reload replaces it, so a popular entry never makes anyone wait. An entry that
isn't read in the window simply expires.

The window must be shorter than `Duration` and `SlidingExpiration`, or every read would reload the entry: in code
(`Build()` throws an `InvalidOperationException`) and in `appsettings.json` (startup fails) a longer one is rejected.
When settings from different places combine into such a window, for example an attribute's `RefreshAhead` with a
shorter `Duration` from configuration, the reload starts halfway through the entry's life instead.

The reload outlives the request that triggered it. For a service registered as scoped or transient (a repository
with a scoped `DbContext`, say), it runs on a new instance from a new DI scope, which is disposed when the reload ends,
never on the request's instance, whose scope may already be gone. That instance has none of the request's state: no
`HttpContext`, and no values the request set on the instance or in `AsyncLocal`. Each scoped or transient cached
service the reload calls gets an instance this way, also from a manual `GetOrCreateAsync` factory, and every scope is
disposed when the reload ends; a call the factory leaves running after it returned keeps the instance it was given.
Singletons reload on their one instance. A `CancellationToken` parameter of the method still cancels the reload.

## Versions

`Version` is part of every key the method produces. Raise it when the shape of the cached result changes, for example
after adding a property, so values cached by the previous deployment are never returned. The old entries expire on
their own.

## Spreading out expiry

Entries created at the same moment, for example right after a deploy, also expire together.
`MethodCacheMemoryOptions.JitterMaxDuration` adds a random extra lifetime to each entry to spread them out
([Memory cache](memory-cache.md)).

## Removing entries early

Expiration is for data that may be a little old. When you know data changed, remove it right away with
[invalidation](invalidation.md).
