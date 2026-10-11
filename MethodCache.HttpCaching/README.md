# MethodCache.HttpCaching

HTTP caching for `HttpClient`, the way a browser does it: responses are cached according to `Cache-Control`,
revalidated with `ETag` / `Last-Modified`, and a `304 Not Modified` comes back to your code as the full cached
response. It follows RFC 9111.

```csharp
using MethodCache.HttpCaching;

builder.Services.AddHttpClient<IGitHubClient, GitHubClient>()
    .AddHttpCaching();
```

That is all. Your typed client keeps calling `GetAsync` as before.

## What it does

| The server sends | What happens on the next request |
|---|---|
| `Cache-Control: max-age=60` | Served from cache for 60 seconds, no network call |
| `ETag: "abc"` (or `Last-Modified`) | Sent with `If-None-Match: "abc"`; a 304 is answered from the cache |
| both | Served from cache while fresh, then revalidated with the ETag |
| `Vary: Accept-Language` | One cached response per language (up to `MaxVariantsPerUrl`) |
| `no-store` | Never cached |

- A successful `POST`, `PUT`, `PATCH` or `DELETE` removes the cached responses for that URL (and for same-origin
  `Location` / `Content-Location`), so you never read your own stale data after a write.
- Cached responses carry an `Age` header, as the standard requires.
- Each HttpClient has its own settings and its own entries.
- Request directives are honored: `no-cache`, `no-store`, `max-age`, `max-stale`, `min-fresh` and `only-if-cached`.

## Settings

Settings are per HttpClient:

```csharp
builder.Services.AddHttpClient<IWeatherClient, WeatherClient>()
    .AddHttpCaching(options =>
    {
        options.EnableStaleIfError = true;           // serve the last response when the API is down
        options.EnableStaleWhileRevalidate = true;   // when the server sends stale-while-revalidate
        options.AddDiagnosticHeaders = true;         // X-Cache: HIT / MISS / REVALIDATED / STALE
    });
```

| Option | Default | |
|---|---|---|
| `IsSharedCache` | `false` | `false`: a private cache; `Authorization` is part of the key so callers never see each other's responses. `true`: a shared cache; `private` and authorized responses are not stored, `s-maxage` applies |
| `DefaultMaxAge` | `null` | Freshness for a response with only an `ETag`/`Last-Modified`. `null` revalidates every time |
| `AllowHeuristicFreshness` | `true` | 10% of the time since `Last-Modified`, at most `MaxHeuristicFreshness` (1 hour) |
| `StaleRetention` | 24 hours | How long a stale entry is kept for revalidation and the stale options below |
| `EnableStaleWhileRevalidate` | `false` | Serve stale at once and refresh in the background, within the server's `stale-while-revalidate` |
| `EnableStaleIfError` | `false` | Serve stale when the request fails or returns 5xx, within `stale-if-error` or `MaxStaleIfError` (24 hours) |
| `MaxVariantsPerUrl` | 5 | `Vary` variants kept per URL |
| `MaxResponseSize` | 10 MB | Larger responses are not cached |
| `KeyHeaders` | empty | Request headers that are always part of the key, for APIs that vary on a header without sending `Vary` |
| `CacheableMethods` | GET, HEAD | |
| `AddDiagnosticHeaders` | `false` | Adds `X-Cache`, `X-Cache-Age` and `X-Cache-TTL` |

`must-revalidate` (and `proxy-revalidate` / `s-maxage` in a shared cache) always wins: such responses are never
served stale.

## Storage

Responses are kept in memory, in a cache of their own (your app's `IMemoryCache` is not touched). The total size
is shared by all clients:

```csharp
builder.Services.Configure<HttpCacheStorageOptions>(o => o.MaxCacheSize = 50 * 1024 * 1024); // default 100 MB
```

To store responses elsewhere, register your own `IHttpCacheStorage` before `AddHttpCaching()`.
