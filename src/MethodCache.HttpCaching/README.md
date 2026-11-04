# MethodCache.HttpCaching

HTTP caching for `HttpClient`, the way a browser does it: responses are cached according to `Cache-Control`,
revalidated with `ETag` / `Last-Modified`, and a `304 Not Modified` comes back to your code as the full cached
response. It follows RFC 9111.

Install it at the same version as `MethodCache`.

Run the [console tour's HttpClient chapter](https://github.com/eoniclabs/MethodCache/tree/main/samples/MethodCache.Demo#httpclient-caching) to see cache hits, ETag validation, `no-store`
and PUT invalidation against a real local HTTP server.

<!-- snippet: http-basic -->
```cs
builder.Services.AddMethodCache();
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
- Each HttpClient has its own settings and its own entries, also when caching is added to every client with
  `ConfigureHttpClientDefaults`.
- Redirects the transport follows are not stored under the URL you asked for, and a retry of the same request gets a
  full response, not the cache's own 304. A `GET` with a body goes to the origin.
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
        options.AddDiagnosticHeaders = true;         // Cache-Status: MethodCache; hit; ttl=42
    });
```
<!-- endSnippet -->

| Option | Default | |
|---|---|---|
| `IsSharedCache` | `false` | `false`: a private cache; `Authorization` and `Cookie` are part of the key so callers never see each other's responses. `true`: a shared cache; `private` and authorized responses are not stored, `s-maxage` applies, and a response stored for an anonymous request is served to every caller (see [Security](https://github.com/eoniclabs/MethodCache/blob/main/docs/security.md#methodcachehttpcaching)) |
| `DefaultMaxAge` | `null` | Freshness for a response with only an `ETag`/`Last-Modified`. `null` revalidates every time |
| `AllowHeuristicFreshness` | `true` | A response with only `Last-Modified` stays fresh for 10% of the time since then, at most `MaxHeuristicFreshness` |
| `MaxHeuristicFreshness` | 1 hour | Upper limit for heuristic freshness |
| `StaleRetention` | 24 hours | How long a stale entry is kept for revalidation and the stale options below |
| `EnableStaleWhileRevalidate` | `false` | Serve stale at once and refresh in the background, within the server's `stale-while-revalidate` |
| `BackgroundRevalidationTimeout` | 10 seconds | Limit for a stale-while-revalidate refresh |
| `EnableStaleIfError` | `false` | Serve stale when the request fails or returns 5xx, within `stale-if-error` or `MaxStaleIfError` |
| `MaxStaleIfError` | 24 hours | How long a response may be stale and still be served on error, when the server sends no `stale-if-error` |
| `MaxResponseBytes` | 10 MiB | Larger responses are not cached. A body without `Content-Length` is read up to this size, then streamed to the caller uncached; dispose the response to release it. Must be positive; `long.MaxValue` means no limit |
| `KeyHeaders` | empty | Request headers that are always part of the key, for APIs that vary on a header without sending `Vary` |
| `AddDiagnosticHeaders` | `false` | Adds a `MethodCache` member to the RFC 9211 `Cache-Status` header, after any the origin's caches added: `MethodCache; hit; ttl=42` from the cache, or `MethodCache; fwd=uri-miss; fwd-status=200; stored` from the origin (`fwd` is `uri-miss`, `vary-miss`, `stale`, `request`, `method` or `bypass`). A stale response adds `detail=stale-while-revalidate` or `detail=stale-if-error`. `Age` is set either way |
| `KeepCookies` | `false` | Keep the transport's automatic cookies and turn response caching off for this client; `only-if-cached` returns 504. Successful writes still invalidate |
| `Tags` | empty | Extra tags on this client's responses, next to its own `http:` tag, for invalidating several clients together |
| `AllowUnverifiedTransport` | `false` | Cache through a transport other than `SocketsHttpHandler` or `HttpClientHandler`. Only when it sends no automatic cookies or hidden caller identity |

Only GET and HEAD responses are cached. OPTIONS and TRACE go to the origin and change nothing; any other method,
including one the cache doesn't know, invalidates the URL's cached responses when it succeeds. `no-cache` (from the
server or in the request) always requires a successful revalidation, even with the stale options on. If reading a response body fails, the request fails; you never get a partly read body.

See [HTTP caching](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/http-caching.md) for the freshness and revalidation rules, how
writes invalidate, and how `AddHttpCaching()` handles cookies and transports.

## Storage

Responses are kept in memory, in a budget of their own next to method results (your app's `IMemoryCache` is not
touched). The total size, headers included, is shared by all clients; a larger response is streamed and not stored:

<!-- snippet: http-storage-size -->
```cs
builder.Services.AddMethodCache(cache => cache.Memory(o => o.MaxHttpBytes = 50 * 1024 * 1024)); // default 100 MB
```
<!-- endSnippet -->

### Sharing responses between instances

With Redis, SQL Server or PostgreSQL configured in `AddMethodCache`, responses are stored there instead, so every
instance shares them: an `ETag` one instance fetched is revalidated by another, and a successful write invalidates the
URL for all of them, even when backplane messages are lost. Your application's own `IDistributedCache` is never used.

<!-- snippet: http-distributed -->
```cs
builder.Services.AddMethodCache(cache => cache.UseRedis("localhost:6379"));
```
<!-- endSnippet -->

If a write's invalidation can't be confirmed, the call throws `HttpCacheInvalidationException`. The write may have
succeeded at the origin, so don't repeat it automatically.

### Administration

`ICacheAdministrator` and the admin endpoints list and remove cached responses, without bodies, credentials, cookies
or query strings. Each client's responses carry the tag `http:` followed by `MethodCacheKeys.Part(clientName)`, plus
the tags in `HttpCacheOptions.Tags` (at most 125, each at most 1024 characters). The
[HTTP caching reference](https://github.com/eoniclabs/MethodCache/blob/main/docs/reference/http-caching.md) has the
details.

### Time

Freshness, eviction from the memory store and the background revalidation timeout all read the time from the
`TimeProvider` registered in the container, if there is one. Register a fake `TimeProvider`, for example from
`Microsoft.Extensions.TimeProvider.Testing`, to test expiry without waiting.

## Metrics

Every request is counted on the `MethodCache` meter, so `AddMethodCacheInstrumentation()` from
MethodCache.OpenTelemetry (or `AddMeter("MethodCache")`) picks them up:

| Instrument | Tags |
|---|---|
| `methodcache.requests` | `methodcache.kind=http`, `methodcache.http.client` (the static client name), `methodcache.result` (`hit`, `miss`, `revalidated`, `stale`, `bypass`, `error`) |
| `methodcache.load.duration` (s) | `methodcache.kind=http`, `methodcache.http.client` for actual origin work, including background refresh |
| `methodcache.http.size` (By) | Bytes the cached responses take in memory |

Invalid settings fail when the host starts: `MaxResponseBytes` must be positive (`long.MaxValue` for no limit),
durations can't be negative, and `BackgroundRevalidationTimeout` must be positive and at most about 49 days.
