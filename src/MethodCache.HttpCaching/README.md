# MethodCache.HttpCaching

Requires .NET 10 and the .NET 10 SDK; use the same package version as `MethodCache`.

Install this package and `MethodCache` at the exact same version. Integration packages use Core internals;
do not override their exact Core dependency. Our package smoke tests promote NU1608 to a restore error.

HTTP caching for `HttpClient`, the way a browser does it: responses are cached according to `Cache-Control`,
revalidated with `ETag` / `Last-Modified`, and a `304 Not Modified` comes back to your code as the full cached
response. It follows RFC 9111.

Run the [console tour's HttpClient chapter](../../samples/MethodCache.Demo/#httpclient-caching) to see cache hits, ETag validation, `no-store`
and PUT invalidation against a real local HTTP server with checked origin-call counters.

<!-- snippet: http-basic -->
```cs
builder.Services.AddMethodCache();
builder.Services.AddHttpClient<IGitHubClient, GitHubClient>()
    .AddHttpCaching();
```
<!-- endSnippet -->

That is all. Your typed client keeps calling `GetAsync` as before.

Responses without custom key headers or `Vary` fields use direct variant selection, avoiding temporary sets
and sorting. Private caches still isolate `Authorization` and `Cookie`; configured headers and `Vary` retain
their combined, case-insensitive selection rules.

A full replacement response also retires the previous cached representation when it cannot be stored, including `no-store`, shared-cache `private`, a body over the size limit, or a failed replacement write. The next caller cannot fall back to an older fresh body. Stale-while-revalidate respects explicit request `max-age`, `min-fresh` and `max-stale` limits.

Concurrent writers with the same variant selection share its namespace, including across processes using distributed storage. The body-free variant index is retained for the maximum supported response lifetime (365 days); individual variants still expire at their own TTL. A change to the selecting headers retires the URL publication epoch exactly once; a concurrent response published after that retirement remains reachable; the current response reaches its caller, and the next request fetches and stores the new schema. If deleting the old entry fails after the epoch was rotated, foreground validation reports that error without rotating again; background validation logs it. A concurrent publication in the new epoch remains reachable.

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
        options.AddDiagnosticHeaders = true;         // X-Cache: HIT, MISS, REVALIDATED, STALE or STALE-IF-ERROR
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
| `MaxResponseSize` | 10 MB | Larger responses are not cached. A body without `Content-Length` is read up to this size, then streamed to the caller uncached; dispose the response to release it |
| `KeyHeaders` | empty | Request headers that are always part of the key, for APIs that vary on a header without sending `Vary` |
| `CacheableMethods` | GET, HEAD | POST, PUT, PATCH and DELETE are never cached, even if added. |
| `AddDiagnosticHeaders` | `false` | Adds `X-Cache`, `X-Cache-Age` and `X-Cache-TTL` |
| `KeepCookies` | `false` | Preserve transport cookies and bypass all response caching; only-if-cached returns 504. Unsafe mutations still invalidate |
| `Tags` | empty | Static tags in addition to the escaped client tag |
| `AllowUnverifiedTransport` | `false` | Cache through a transport other than `SocketsHttpHandler` or `HttpClientHandler`. Only when it sends no automatic cookies or hidden caller identity |

`no-cache` (from the server or in the request) always requires a successful revalidation, even with the stale
options on. If reading a response body fails, the request fails; you never get a partly read body.

See [HTTP caching](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/http-caching.md) for the freshness and revalidation rules, how
writes invalidate, Core-owned storage, and how `AddHttpCaching()` handles cookies and transports.

## Storage

Responses are kept in memory, in a cache of their own (your app's `IMemoryCache` is not touched). The total size
is shared by all clients:

<!-- snippet: http-storage-size -->
```cs
builder.Services.AddMethodCache(cache => cache.Memory(o => o.MaxHttpBytes = 50 * 1024 * 1024)); // default 100 MB
```
<!-- endSnippet -->

### Sharing responses between instances

Responses use MethodCache's native Redis or SQL Server automatically when configured. Core keeps no local
response or epoch copies in this mode. The application's unrelated `IDistributedCache` is never a fallback.
Without an owned native transport, responses stay in Core's separate HTTP memory store. Register Core explicitly
with `AddMethodCache`; either registration order works.

<!-- snippet: http-distributed -->
```cs
builder.Services.AddMethodCache(cache => cache.UseRedis("localhost:6379"));
```
<!-- endSnippet -->

An ETag fetched by one instance is revalidated by another, and a successful unsafe mutation advances the shared
URL generation before returning. This remains correct without backplane messages. Tag and Clear generations are
shared with Core-only nodes and across entry formats. Graceful async disposal drains owned generation work,
retries queued invalidations once through an open circuit breaker, and reports any lost count. Recovery is not
durable across process exit. A failed generation read bypasses caching. An unconfirmed
mutation throws `HttpCacheInvalidationException`; the origin may already have succeeded. Do not automatically
repeat the origin mutation. Core owns bounded invalidation recovery after caller cancellation.

`ICacheAdministrator` lists resident local HTTP representations, or a bounded register of at most 1024 historical
L2 observations. `ObservedDistributed` is not verified presence or a global inventory. No bodies, credentials,
cookies or raw queries are kept in that register. Known-id removal retires all URL variants; an unknown id is
NotFound. `Clear` retires the shared generation without enumerating L2 values. Static `HttpCacheOptions.Tags`
and the implicit client tag support tag invalidation. For client names up to 128 UTF-16 code units, that tag is
`http:` plus `MethodCacheKeys.Part(clientName)`; longer names use `http:#:` plus the complete client SHA-256 identity.
Long client names are accepted. Administration shows a bounded prefix plus their hash. Configure at most 125
nonblank custom tags of at most 1024 code units each; invalid tag settings fail host startup.

Local client-tag invalidation scans resident HTTP entries and reclaims matching bodies and byte budget.
URL invalidation and other configured application tags use their indexes. Client-wide invalidation work
therefore grows with the resident entry count while publication avoids the implicit client index.

The former public storage interface/entry/options are removed. HTTP entries use a private codec and are unaffected
by method serializer, TTL, jitter or fail-safe settings. `MaxHttpBytes` includes body, stored headers and conservative
metadata costs; oversized responses remain streamed. The default budget is 100 MiB, separate from method entries.

### Time

Freshness, eviction from the memory store and the background revalidation timeout all read the time from the
`TimeProvider` registered in the container, if there is one. Register a fake `TimeProvider`, for example from
`Microsoft.Extensions.TimeProvider.Testing`, to test expiry without waiting.

## Metrics

Every request is counted on the `MethodCache` meter, so `AddMethodCacheInstrumentation()` from
MethodCache.OpenTelemetry (or `AddMeter("MethodCache")`) picks them up:

| Instrument | Tags |
|---|---|
| `methodcache.requests` | `kind=http`, `name` (static client), `result` (`hit`, `miss`, `revalidated`, `stale`, `bypass`, `error`) |
| `methodcache.load.duration` (s) | `kind=http`, `name` for actual origin work, including background refresh |
| `methodcache.http.size` (By) | Actual resident HTTP storage cost |

HTTP cache freshness includes request/response delay when correcting the upstream Age header, for both initial responses and 304 validation. Age arithmetic saturates instead of overflowing on extreme headers.

Background HTTP revalidation preserves the request HTTP version, version policy and a shallow copy of its request options. Option values keep their original ownership; use values that remain valid for background work.

Named options are validated at host startup: sizes and freshness/retention durations must be non-negative (zero response size remains unlimited), and background revalidation timeout must be positive and at most 4,294,967,294 milliseconds. Stale-if-error respects the caller’s `max-age`, `min-fresh` and `max-stale` constraints.
