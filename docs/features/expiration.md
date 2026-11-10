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

Without any default, entries live for five minutes. `Defaults` and groups are for cached methods; a manual call
([Manual caching](manual-caching.md)) has only the options it passes, so without a `Duration` its entry lives five
minutes. No entry lives longer than
`MethodCacheMemoryOptions.MaxExpiration` (seven days), so a longer `Duration` fails where it is set instead of being
cut short: at startup, in a runtime override or in a manual call. For longer entries, raise the limit, up to 365 days,
with `mc.Memory(o => o.MaxExpiration = ...)`. With your own `ICacheManager`, its own limits apply. See
[Memory cache](memory-cache.md).

### How long an entry lives

What a cached method or a manual call ends up with, by what it sets. A cached method also takes a `Duration` from
`Defaults` or its group when it sets none itself; a manual call takes only its own options.

| Sets | Kept in memory | Kept in the distributed cache |
|---|---|---|
| nothing | 5 minutes | 5 minutes |
| `Duration` | the `Duration` | the `Duration`, at most one day |
| `DurationFor` | the duration the result gives | that duration, at most one day |
| `SlidingExpiration` | while it is read once per window, up to `MaxExpiration` (7 days) | one window from when it was stored |
| `Duration` and `SlidingExpiration` | while it is read once per window, up to the `Duration` | one window from when it was stored, or the `Duration` if that is shorter |

- No entry lives longer than `MaxExpiration` (7 days, at most 365); a longer `Duration` or sliding window fails where
  it is set, at startup for a method and at the call for manual caching.
- The one-day limit in the distributed cache is `TagRetention` (1 day) less `ClockSkewTolerance` (2 seconds), counted
  from when the method started loading the value, so that a tag invalidation always outlives the entries it covers.
  Raise `TagRetention` to share values longer ([Distributed cache](distributed-cache.md#options)). The first value of
  each method, or of each `WithName` name for manual caching, that asks for longer logs a warning that says so.
- An instance that reads a value from the distributed cache keeps it no longer than the copy stored there.
- `FailSafe` keeps an expired value in memory for its window, to serve when a reload fails, on every instance that
  has the value, also one that read it from the distributed cache ([Fail-safe](fail-safe.md)).

## A duration from the result

`DurationFor` gives each result its own duration, in place of `Duration`, such as the time left on a token, a
shorter time for an empty result, or the time until the next row of a schedule takes effect, as with future-dated
prices. Set it in startup code, for one method:

<!-- snippet: expiration-duration-for -->
```cs
builder.Services.AddMethodCache(mc =>
{
    // A token is kept until a minute before it expires; one that expires sooner isn't cached
    mc.Method((ITokenClient c) => c.GetTokenAsync(Any<string>.Value))
        .DurationFor(token => token.ExpiresAt - DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));

    // An empty result is kept for 30 seconds instead of ten minutes
    mc.Method((ISearch s) => s.FindAsync(Any<string>.Value))
        .DurationFor(result => result.Items.Count == 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10));

    // A price is kept until the next one takes effect, so a future-dated price applies on time
    mc.Method((IPriceSchedule s) => s.GetCurrentAsync(Any<string>.Value))
        .DurationFor(price => price.NextChangeAt is { } next ? next - DateTimeOffset.UtcNow : TimeSpan.FromHours(1));
});
```
<!-- endSnippet -->

- A duration of zero or less doesn't cache the result, and neither does a function that throws. The caller still gets
  the result, and the next call runs the method again.
- A null result reaches the function as null, so a method that returns `Task<Price?>` needs
  `price => price is null ? ... : ...`. Null carries no time of its own: when "no row yet" has a known time at which
  one takes effect, such as a price that starts next week, return a type that can say so, such as
  `record CurrentPrice(Price? Price, DateTimeOffset? NextChangeAt)`, or give null a fixed duration. With
  `CacheNullValues = false` a null result isn't cached at all.
- It runs once per load and on each value read from the distributed cache, which keeps the value for the time the
  result gives on every instance. It never runs on a hit in memory.
- No jitter is added ([Spreading out expiry](#spreading-out-expiry)), and `MaxExpiration` still caps it.
  `SlidingExpiration` and `RefreshAhead` count from the duration the result gives, so keep `RefreshAhead` shorter
  than the shortest one. To slide some results and not others, add
  [`SlidingFor`](#a-sliding-window-from-the-result).
- `WithDuration` and `DurationFor` replace each other in code: whichever is called last wins. A `Duration` for the
  method in `appsettings.json` or a runtime override replaces `DurationFor`.
- A `DurationFor` in startup code replaces the `Duration` of the method's `[Cache]` attribute too, so the attribute's
  time doesn't cap it: a cap belongs in the function, such as `left < cap ? left : cap`. `GetMethods()` lists a setting one source replaces in another under `ReplacedSources`
  ([What applies on this instance](configuration.md#what-applies-on-this-instance)).
- In `Defaults` or a group it fails at startup, since it reads one method's result type. So does a `DurationFor<T>`
  for a type the method can't return.
- With manual caching, pass it in the options: `o => o.DurationFor<AccessToken>(token => ...)`
  ([Manual caching](manual-caching.md)).

## Sliding expiration

`SlidingExpiration` keeps an entry while it is read: each read extends its life by the sliding window. It never lives
past its `Duration`; without a `Duration`, only `MaxExpiration` limits it. A cached method can get its `Duration` from
`Defaults` or a group; a manual call only from its own options, so a manual call with only a sliding window is kept as
long as it is read, up to `MaxExpiration`.

So the window must be shorter than the `Duration`, or reads never extend anything: `Duration = "5m"` with
`SlidingExpiration = "1d"` expires each entry five minutes after it was loaded. On one `[Cache]` that is warning
[MC0038](../user-guide/DIAGNOSTICS.md#mc0038). When the two come from different places, such as
`WithSlidingExpiration` on a method and `WithDuration` in `Defaults`, the host logs a warning when it starts that
names the method.

A method that inherits a sliding window from the defaults, a group or configuration turns it off with
`SlidingExpiration = "off"` (`TimeSpan.Zero` in code). Leaving the setting out inherits it. `RefreshAhead` works the
same way.

With a distributed cache, the copy stored there lives one window from when it was stored, or the `Duration` if that is
shorter: reads in one instance's memory don't reach it. An instance that got the value from the distributed cache keeps
it no longer than the stored copy, and the next instance that asks for it after that loads it again. The instance that
loaded the value keeps extending its own copy while it is read, up to the `Duration`, until another instance loads the
value again: a new value replaces the older copies on every instance. So a value that several instances read is loaded
about once per window. To share a value for its whole `Duration`, leave the window out.

### Kept as long as it is read

For a cache that only slides, such as one that replaces an `IMemoryCache` entry with only `SlidingExpiration`, set
the window and no `Duration`: the entry is kept while it is read at least once per window, up to `MaxExpiration`.

<!-- snippet: expiration-sliding-only -->
```cs
public interface ILayouts
{
    // Kept while it is read at least every 30 minutes, up to MaxExpiration (7 days)
    [Cache(SlidingExpiration = "30m")]
    Task<Layout> GetAsync(string name);
}
```
<!-- endSnippet -->

- When `Defaults` or the method's group sets a `Duration`, that still ends the entry. `Duration` can't be turned off,
  so give the method its own `Duration` as long as you accept, such as `Duration = "7d"`.
- To keep entries past seven days, raise `MaxExpiration`, up to 365 days:
  `mc.Memory(o => o.MaxExpiration = TimeSpan.FromDays(30))`.
- A manual call does the same with only `o.WithSlidingExpiration(...)`, since it takes no `Duration` from `Defaults`.

### A sliding window from the result

`SlidingFor` gives each result its own sliding window, in place of `SlidingExpiration`. Zero turns sliding off for that
result, so it lives its duration whether it is read or not. Together with `DurationFor`, a result that loaded can stay
while it is read and one that failed can be tried again after a fixed time:

<!-- snippet: expiration-sliding-for -->
```cs
builder.Services.AddMethodCache(mc =>
    // A preview that loaded is kept while it is read at least every 30 minutes, for at most a day.
    // One that failed is kept for two hours, read or not, and then the page is tried again.
    mc.Method((ILinkPreviews p) => p.GetAsync(Any<string>.Value))
        .DurationFor(preview => preview.Succeeded ? TimeSpan.FromDays(1) : TimeSpan.FromHours(2))
        .SlidingFor(preview => preview.Succeeded ? TimeSpan.FromMinutes(30) : TimeSpan.Zero));
```
<!-- endSnippet -->

- It never keeps a result past its duration, the one `DurationFor` gives or the method's `Duration`.
- A function that throws doesn't cache the result. It runs once per load and on each value read from the distributed
  cache, never on a hit in memory.
- `WithSlidingExpiration` and `SlidingFor` replace each other in code: whichever is called last wins. A
  `SlidingExpiration` for the method in `appsettings.json` or a runtime override replaces `SlidingFor`.
- Like `DurationFor`, it is set for one method, in startup code or in the options of a manual call:
  `o => o.SlidingFor<LinkPreview>(preview => ...)`.

## Refresh-ahead

`RefreshAhead` reloads an entry in the background when it is read within that window before it expires. The caller
gets the cached value at once, and the reload replaces it, so a popular entry never makes anyone wait. An entry that
isn't read in the window simply expires. With a distributed cache the reload also replaces the stored copy, so other
instances read the new value instead of loading it.

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
