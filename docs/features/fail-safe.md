# Fail-safe

Fail-safe keeps an expired value for a while and returns it when calling the method again fails, so a database or API
outage shows slightly old data instead of errors. It is off by default.

## Turning it on

For every cached method at once:

<!-- snippet: failsafe-central -->
```cs
builder.Services.AddMethodCache(config => config.FailSafe(
    duration: TimeSpan.FromHours(1),              // how long an expired value is kept as a fallback
    timeout: TimeSpan.FromMilliseconds(200),      // optional: don't wait longer than this when a fallback exists
    retryAfter: TimeSpan.FromSeconds(30)));       // optional: reuse the fallback this long before trying again
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

- **When the method throws**, the caller gets the expired value if it is younger than `duration`. That value is then
  reused for `retryAfter` (default 30 seconds) before the method is called again, so a failing dependency isn't
  hammered.
- **When the method is slow** and `timeout` is set, the caller gets the expired value after `timeout` and the call
  finishes in the background; its result replaces the old value. Without an expired value the caller waits as usual.
- A caller's own cancellation is never treated as a failure, so it doesn't trigger fail-safe.
- Without an expired value, the exception reaches the caller as usual.

## Per method

A method or group can change any of it, and its own setting wins over the central one:

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

In code, use the same builder method: `Configure(o => o.FailSafe(TimeSpan.FromHours(1)))`. `o.FailSafe(TimeSpan.Zero)`
turns it off. In `appsettings.json`, a method takes `FailSafe` (a time span or `"off"`), `FailSafeTimeout` and
`FailSafeRetryAfter`.

## Limits

The fallback lives in each instance's memory, so right after a restart there is none until the method has succeeded
once.
