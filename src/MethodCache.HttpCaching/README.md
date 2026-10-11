# MethodCache.HttpCaching

HTTP caching for `HttpClient`, the way a browser does it: responses are cached according to `Cache-Control`,
revalidated with `ETag` / `Last-Modified`, and a `304 Not Modified` comes back to your code as the full cached
response. It follows RFC 9111.

<!-- snippet: http-basic -->
```cs
builder.Services.AddHttpClient<IGitHubClient, GitHubClient>()
    .AddHttpCaching();
```
<!-- endSnippet -->

That is all. Your typed client keeps calling `GetAsync` as before.

## What it does

| The server sends | What happens on the next request |
|---|---|
| `Cache-Control: max-age=60` | Served from cache for 60 seconds, no network call |
| `ETag: "abc"` (or `Last-Modified`) | Sent with `If-None-Match: "abc"`; a 304 is answered from the cache |
| both | Served from cache while fresh, then revalidated with the ETag |
| `Vary: Accept-Language` | One cached response per language |
| `no-store` | Never cached |

- A successful `POST`, `PUT`, `PATCH` or `DELETE` removes the cached responses for that URL (and for same-origin
  `Location` / `Content-Location`) for every caller, language and key header value, so nobody reads stale data
  after a write.
- Cached responses carry an `Age` header, as the standard requires.
- `Set-Cookie` is never stored: the caller the server answered gets it, nobody served from the cache does.
- Each HttpClient has its own settings and its own entries.
- Request directives are honored: `no-cache` (revalidates and stores the answer), `no-store`, `max-age`, `max-stale`, `min-fresh` and `only-if-cached`.
- `Range` requests go to the origin, and `206 Partial Content` is never stored.
- Requests that carry their own `If-None-Match` or `If-Modified-Since` go to the origin, so your `304` reaches you.
- Statuses that are cacheable by default (`200`, `203`, `204`, `301`, `404`, ...) are stored; other `2xx` responses only when they say how long they stay fresh (`max-age` or `Expires`).

## Settings

Settings are per HttpClient:

<!-- snippet: http-settings -->
```cs
builder.Services.AddHttpClient<IWeatherClient, WeatherClient>()
    .AddHttpCaching(options =>
    {
        options.EnableStaleIfError = true;           // serve the last response when the API is down
        options.EnableStaleWhileRevalidate = true;   // when the server sends stale-while-revalidate
        options.AddDiagnosticHeaders = true;         // X-Cache: HIT / MISS / REVALIDATED / STALE
    });
```
<!-- endSnippet -->

| Option | Default | |
|---|---|---|
| `IsSharedCache` | `false` | `false`: a private cache; `Authorization` and `Cookie` are part of the key so callers never see each other's responses. `true`: a shared cache; `private` and authorized responses are not stored, `s-maxage` applies, and a response stored for an anonymous request is served to every caller (see [Security](../../docs/security.md#methodcachehttpcaching)) |
| `DefaultMaxAge` | `null` | Freshness for a response with only an `ETag`/`Last-Modified`. `null` revalidates every time |
| `AllowHeuristicFreshness` | `true` | 10% of the time since `Last-Modified`, at most `MaxHeuristicFreshness` (1 hour) |
| `StaleRetention` | 24 hours | How long a stale entry is kept for revalidation and the stale options below |
| `EnableStaleWhileRevalidate` | `false` | Serve stale at once and refresh in the background, within the server's `stale-while-revalidate` |
| `EnableStaleIfError` | `false` | Serve stale when the request fails or returns 5xx, within `stale-if-error` or `MaxStaleIfError` (24 hours) |
| `MaxResponseSize` | 10 MB | Larger responses are not cached. A body without `Content-Length` is read up to this size, then streamed to the caller uncached |
| `KeyHeaders` | empty | Request headers that are always part of the key, for APIs that vary on a header without sending `Vary` |
| `CacheableMethods` | GET, HEAD | POST, PUT, PATCH and DELETE are never cached, even if added. |
| `AddDiagnosticHeaders` | `false` | Adds `X-Cache`, `X-Cache-Age` and `X-Cache-TTL` |

`no-cache` requires successful validation, including when stale-while-revalidate or stale-if-error is enabled.
A request with `no-cache` also requires validation and propagates origin errors. `must-revalidate` (and
`proxy-revalidate` / `s-maxage` in a shared cache) forbids serving stale responses.

## Storage

Responses are kept in memory, in a cache of their own (your app's `IMemoryCache` is not touched). The total size
is shared by all clients:

<!-- snippet: http-storage-size -->
```cs
builder.Services.Configure<HttpCacheStorageOptions>(o => o.MaxCacheSize = 50 * 1024 * 1024); // default 100 MB
```
<!-- endSnippet -->

### Sharing responses between instances

With several instances of your app, each one would otherwise fetch and revalidate on its own. Turn on
`UseDistributedCache` and responses are kept in the registered `IDistributedCache` instead of memory, the same
Redis or SQL Server that MethodCache uses:

<!-- snippet: http-distributed -->
```cs
builder.Services.AddMethodCacheRedis("localhost:6379");
builder.Services.Configure<HttpCacheStorageOptions>(o => o.UseDistributedCache = true);
```
<!-- endSnippet -->

Now an ETag fetched by one instance is revalidated (and usually answered with a 304) by all of them, and a POST
through one instance invalidates the cached GET for all of them. If the distributed cache is down, requests go
straight to the origin. Apps that share one Redis or SQL Server should each set their own `KeyPrefix`
(default `methodcache-http:`). `ClearAsync()` cannot clear a distributed cache; entries there expire on their own.

To store responses somewhere else entirely, register your own `IHttpCacheStorage` before `AddHttpCaching()`.

### Time

Freshness, eviction from the memory store and the background revalidation timeout all read the time from the
`TimeProvider` registered in the container, if there is one. Register a fake `TimeProvider`, for example from
`Microsoft.Extensions.TimeProvider.Testing`, to test expiry without waiting.

## Metrics

Every request is counted on the `MethodCache` meter, so `AddMethodCacheInstrumentation()` from
MethodCache.OpenTelemetry (or `AddMeter("MethodCache")`) picks them up:

| Instrument | Tags |
|---|---|
| `methodcache.http.requests_total` | `client`, `outcome` (`hit`, `miss`, `revalidated`, `stale`, `bypass`, `error` when the request threw) |
| `methodcache.http.request_duration` (ms) | `client`, `outcome` |
