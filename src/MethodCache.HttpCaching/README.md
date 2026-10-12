# MethodCache.HttpCaching

HTTP caching for `HttpClient`, the way a browser does it: responses are cached according to `Cache-Control`,
revalidated with `ETag` / `Last-Modified`, and a `304 Not Modified` comes back to your code as the full cached
response. It follows RFC 9111.

Run the [console tour's HttpClient chapter](../../samples/MethodCache.Demo/#httpclient-caching) to see cache hits, ETag validation, `no-store`
and PUT invalidation against a real local HTTP server with checked origin-call counters.

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

A 304 also updates storage policy: `no-store`, `Vary: *` or shared-cache `private` removes the old response,
including during background revalidation. Omitted `Vary` keeps the original variant selection.

An in-flight GET or background revalidation that started before a successful write cannot publish a response
into the new cache generation, including across instances. Publication generations are separate from response
writers. The built-in store bounds this metadata to 4096 slots per storage prefix; a slot collision can cause an
additional cache miss for another URL. Generation records use maximum absolute expiration; if the backend
evicts one anyway, a fresh generation starts, so the loss only causes misses and never serves a response from
before the write. Read failures fall back to the origin; invalidation write failures propagate after
the origin write, which may already have succeeded. Custom storage implementations implement
`GetGenerationAsync` and `InvalidateAsync` with shared, unique generations. See [HTTP caching](../../docs/features/http-caching.md).

Cacheable bodies with an accurate `Content-Length` of up to 1 MB use one owned array. Larger, unknown or
inaccurate lengths use pooled temporary storage plus the final array; a declared length never allocates more than
1 MB up front, and a HEAD response allocates nothing for the `Content-Length` it carries. An oversized body remains readable as a stream; dispose the
response to release its storage.

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
`UseDistributedCache` and responses use MethodCache's own distributed cache from `AddMethodCacheRedis` or
`AddMethodCacheSqlServer`. If neither is registered, they use the application's `IDistributedCache`. The
application cache registration stays unchanged. Responses are kept outside memory, with the same
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

To store responses somewhere else entirely, register your own `IHttpCacheStorage` before `AddHttpCaching()`:

<!-- snippet: http-custom-storage-registration -->
```cs
builder.Services.AddSingleton<IHttpCacheStorage>(new DictionaryHttpCacheStorage(TimeProvider.System));
builder.Services.AddHttpClient<IGitHubClient, GitHubClient>()
    .AddHttpCaching();
```
<!-- endSnippet -->

A custom storage must implement `GetGenerationAsync` and `InvalidateAsync`: a successful POST, PUT, PATCH or
DELETE now invalidates only through `InvalidateAsync` (it used to call `RemoveAsync`), so a storage that implements
it as a no-op keeps serving old responses after a write. A minimal correct storage:

<!-- snippet: http-custom-storage -->
```cs
public sealed class DictionaryHttpCacheStorage(TimeProvider time) : IHttpCacheStorage
{
    private readonly ConcurrentDictionary<string, (HttpCacheEntry Entry, DateTimeOffset Expires)> _entries = new();
    private readonly ConcurrentDictionary<string, string> _generations = new();

    // Responses are stored per generation. A missing generation (never set, cleared or lost) starts a new
    // unique one, never a value used before, so losing it can only cause cache misses.
    public ValueTask<string> GetGenerationAsync(string key, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_generations.GetOrAdd(key, _ => Guid.NewGuid().ToString("N")));

    // Called after a successful POST, PUT, PATCH or DELETE: a fresh generation hides every older response.
    public ValueTask InvalidateAsync(string key, CancellationToken cancellationToken = default)
    {
        _generations[key] = Guid.NewGuid().ToString("N");
        return ValueTask.CompletedTask;
    }

    public ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_entries.TryGetValue(key, out var stored) && stored.Expires > time.GetUtcNow() ? stored.Entry : null);

    public ValueTask SetAsync(string key, HttpCacheEntry entry, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        _entries[key] = (entry, time.GetUtcNow() + timeToLive);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _entries.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        _generations.Clear(); // in-flight requests can then only publish into orphaned generations
        _entries.Clear();
        return ValueTask.CompletedTask;
    }
}
```
<!-- endSnippet -->

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

## Behavior and compatibility

A failed response-body read fails the request rather than returning a partially consumed body. Buffered and streamed responses release their original content resources on completion, cancellation or disposal.

`only-if-cached` never contacts the origin, including range requests, caller validators, `no-store`, unsupported methods and background revalidation. An unavailable representation returns 504.

HTTP cache keys preserve individual header-value boundaries, including `Cookie` and custom tenant headers.

An explicit `Expires` at or before the response date is stale immediately; heuristic freshness and `DefaultMaxAge` do not extend it.

An invalid `Expires` date (including `Expires: 0`) is immediately stale, even when heuristic freshness or a default lifetime is enabled. Explicit `max-age` and `s-maxage` take precedence.


Unknown HTTP transports (including opaque wrappers and native platform handlers) bypass caching by default and emit one warning per named client. Set `AllowUnverifiedTransport` only when you have verified that the whole transport sends no automatic cookies or hidden caller identity. Recognized transports with automatic cookies always bypass.

`AddHttpCaching()` disables automatic cookies on the named client’s `SocketsHttpHandler` or `HttpClientHandler`, after all primary-handler configuration. The default registration therefore caches immediately. Send caller cookies explicitly; a handler that manages a hidden cookie jar belongs on a client without HTTP caching. Other named clients are unaffected.

Oversized HTTP responses remain streamed; disposing them closes both stream segments and the original content owner, even if a custom owner does not close its stream.

HTTP key framing changes all response cache keys, including requests without `Vary`; old entries become cold misses. See the [release compatibility notes](../../docs/developer/RELEASE_NOTES.md).

`AddHttpCaching()` runs the cache after all additional handlers, so injected caller identity and rewritten requests are visible before lookup. Delegating handlers inside the primary transport, or below a directly constructed cache handler, are unverified and bypass caching unless the entire chain is explicitly trusted.
