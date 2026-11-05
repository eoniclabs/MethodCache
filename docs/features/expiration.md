[← MethodCache documentation](../README.md)

# Expiration

Each cached method decides how long its entries live. The settings below can be set on `[Cache]`, in startup code, in
`appsettings.json` or at runtime ([Configuration](configuration.md)).

Chapter 7 of the [console demo](../../samples/MethodCache.Demo/) runs absolute expiry, sliding windows and
refresh-ahead with an explicitly advanced clock and checked source-call counts.

<!-- snippet: expiration-attributes -->
```cs
public interface IReportService
{
    // Gone ten minutes after it was stored
    [Cache(Duration = "10m")]
    Task<Report> GetAsync(int year);

    // Kept while it is read at least every five minutes, but never longer than an hour
    [Cache(Duration = "1h", SlidingExpiration = "5m")]
    Task<Session> GetSessionAsync(string id);

    // Reloaded in the background when it is read in its last minute, so callers never wait
    [Cache(Duration = "10m", RefreshAhead = "1m")]
    Task<PriceList> GetPricesAsync(string region);

    // Changing the version changes every key, so values cached by the previous version are ignored
    [Cache(Duration = "1d", Version = 2)]
    Task<Report> GetYearlyAsync(int year);
}
```
<!-- endSnippet -->

## Writing a duration

Attributes, `appsettings.json` and runtime configuration read durations the same way: a number and a unit, such as
`"10m"` or `"24h"`.

| Write | Means | Example |
|---|---|---|
| a number and `s` | seconds | `"30s"`, `"0.5s"` |
| a number and `m` | minutes | `"5m"`, `"1.5m"` |
| a number and `h` | hours | `"24h"`, `"1.5h"` |
| a number and `d` | days | `"1d"`, `"7d"` |
| `"off"` | turned off (`SlidingExpiration`, `RefreshAhead` and `FailSafe` only) | `"off"` |

- Units are case-insensitive and the number uses a `.` for decimals, whatever the culture.
- Milliseconds are written as seconds (`"0.25s"`). There is no `ms` unit.
- A bare number (`"300"`), zero, a negative value, combined units (`"1h30m"`) and a `TimeSpan` (`"00:05:00"`) are
  errors: at build time in an attribute ([MC0004](../user-guide/DIAGNOSTICS.md#mc0004), whose code fix rewrites a
  `TimeSpan` as `"5m"`), and at startup in `appsettings.json`, with the value to write instead.
- In code, pass a `TimeSpan`; `TimeSpan.Zero` turns `SlidingExpiration`, `RefreshAhead` or `FailSafe` off.

## Duration

`Duration` is how long an entry lives after it was stored. A method without one uses the global default:

<!-- snippet: expiration-default -->
```cs
builder.Services.AddMethodCache(config =>
    config.Defaults(o => o.WithDuration(TimeSpan.FromMinutes(15))));
```
<!-- endSnippet -->

Without any default, entries live for five minutes. No entry lives longer than
`MethodCacheMemoryOptions.MaxExpiration` (seven days), so a longer `Duration` fails where it is set instead of being
cut short: at startup, in a runtime override or in a manual call. For longer entries, raise the limit, up to 365 days,
with `mc.Memory(o => o.MaxExpiration = ...)`. With your own `ICacheManager`, its own limits apply. See
[Memory cache](memory-cache.md).

## Sliding expiration

`SlidingExpiration` keeps an entry while it is read: each read extends its life by the sliding window. It never lives
past its `Duration`; without a `Duration`, only `MaxExpiration` limits it.

A method that inherits a sliding window from the defaults, a group or configuration turns it off with
`SlidingExpiration = "off"` (`TimeSpan.Zero` in code). Leaving the setting out inherits it. `RefreshAhead` works the
same way.

## Refresh-ahead

`RefreshAhead` reloads an entry in the background when it is read within that window before it expires. The caller
gets the cached value at once, and the reload replaces it, so a popular entry never makes anyone wait. An entry that
isn't read in the window simply expires.

The window must be shorter than `Duration` and `SlidingExpiration`, or every read would reload the entry: on `[Cache]`
(build error [MC0023](../user-guide/DIAGNOSTICS.md#mc0023)), in code (`Build()` throws an `InvalidOperationException`)
and in `appsettings.json` (startup fails) a longer one is rejected.
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

> [!TIP]
> Expiration is for data that may be a little old. When you know data changed, remove it right away with
> [invalidation](invalidation.md).
