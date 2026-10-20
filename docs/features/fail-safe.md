[← MethodCache documentation](../README.md)

# Fail-safe

Fail-safe keeps an expired value for a while and returns it when calling the method again fails, so a database or API
outage shows slightly old data instead of errors. It is off by default.

Chapter 8 of the [console demo](../../samples/MethodCache.Demo/) shows a failing API, retry suppression,
recovery and a slow response that finishes after the caller receives its fallback. A fake clock controls the
expiry, retry and timeout windows; the same scenario runs in the docs tests.

## Turning it on

For every cached method at once:

<!-- snippet: failsafe-central -->
```cs
builder.Services.AddMethodCache(config => config.Defaults(o => o
    .WithFailSafe(TimeSpan.FromHours(1))                    // how long an expired value is kept as a fallback
    .WithFailSafeTimeout(TimeSpan.FromMilliseconds(200))    // optional: don't wait longer than this when a fallback exists
    .WithFailSafeRetryAfter(TimeSpan.FromSeconds(30))));    // optional: reuse the fallback this long before trying again
```
<!-- endSnippet -->

or in `appsettings.json`:

```json
{
  "MethodCache": {
    "FailSafe": { "Duration": "01:00:00", "Timeout": "00:00:00.200", "RetryAfter": "00:00:30" }
  }
}
```

## What happens

- **When the method throws**, the caller gets the expired value if it is younger than the fail-safe duration. That
  value is then reused for the retry delay (`RetryAfter`, default 30 seconds) before the method is called again, so a
  failing dependency isn't hammered; no [refresh-ahead](expiration.md#refresh-ahead) reload starts during that time
  either.
- **When the method is slow** and a `Timeout` is set, the caller gets the expired value after it and the call
  finishes in the background; its result replaces the old value. Without an expired value the caller waits as usual.
  Because the call may outlive the request, a scoped or transient service makes it on a new instance from a new DI
  scope, disposed when the call ends, whenever a fallback exists and a `Timeout` is set (the instance has none of the
  request's own state, such as values set on the instance). The caller's `HttpContext` and `AsyncLocal` values still
  flow to it, as it starts on the caller's call. Singletons use their one instance.
- A caller's own cancellation is never treated as a failure, so it doesn't trigger fail-safe.
- A value that was invalidated (its key, a tag or a clear) or replaced is never a fallback, also when that happened
  while the reload ran: callers then wait for the reload, or get its exception.
- Without an expired value, the exception reaches the caller as usual.
- `GetOrCreateManyAsync` works the same way for each key: when the batch factory throws, each key gets its own
  fallback, and the exception is thrown only when a key has none.

## Per method

A method or group can change any of it, and its own setting wins over the central one. `[Cache]` takes the same three
settings as `FailSafe`, `FailSafeTimeout` and `FailSafeRetryAfter`:

<!-- snippet: failsafe-per-method -->
```cs
public interface IAccountService
{
    [Cache(Duration = "00:05:00", FailSafe = "01:00:00", FailSafeTimeout = "00:00:00.200")]
    Task<Rates> GetRatesAsync();

    [Cache(FailSafe = "off")]   // a balance must never be old, so an error is better
    Task<decimal> GetBalanceAsync(int accountId);
}
```
<!-- endSnippet -->

In code, use the builder methods with the same names: `WithFailSafe`, `WithFailSafeTimeout` and
`WithFailSafeRetryAfter`, for example `Configure(o => o.WithFailSafeTimeout(TimeSpan.FromMilliseconds(100)))` to
change only the timeout. `o.WithFailSafe(TimeSpan.Zero)` turns fail-safe off. In `appsettings.json`, a method takes
`FailSafe` (a time span or `"off"`), `FailSafeTimeout` and `FailSafeRetryAfter`.

## Limits

> [!NOTE]
> The fallback lives in each instance's memory, so right after a restart there is none until the method has succeeded
> once.

Caller cancellation stops the wait during an L2 read or a slow factory, including bulk reads and fail-safe waits. L2 reads pass cancellation to the backend; shared factory work may finish for other callers. Caller cancellation is not reported as a dependency failure.
