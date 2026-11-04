[← MethodCache documentation](../README.md)

# HTTP response caching

Add `AddHttpCaching()` to an `HttpClient` registration to cache HTTP responses according to their response headers,
as RFC 9111 describes. Freshness comes from `Cache-Control` and expiry headers; `ETag` and `Last-Modified` enable
conditional validation, and a successful 304 is returned to the caller as the full response.

Chapter 11 of the [console tour](../../samples/MethodCache.Demo/#httpclient-caching) starts a local HTTP server and checks cache hits, ETag/304 validation, `no-store` and invalidation after PUT. It needs no external API or Docker.

<!-- snippet: http-basic -->
```cs
builder.Services.AddMethodCache();
builder.Services.AddHttpClient<IGitHubClient, GitHubClient>()
    .AddHttpCaching();
```
<!-- endSnippet -->

The [package guide](../../src/MethodCache.HttpCaching/README.md) covers settings, private and shared caches, limits
and metrics. This page covers the rules the cache follows. [Security](../security.md#methodcachehttpcaching) has the
threat model, and the [HTTP caching reference](../reference/http-caching.md) the exact behavior.

## Freshness and validation

- Stale-while-revalidate and stale-if-error are optional. Response `no-cache` requires successful validation before
  the response is reused, even when either option is on. Request `no-cache` has the same requirement: a 5xx response
  or network error reaches the caller rather than falling back to cached content.
- `must-revalidate` forbids serving a stale response, as do `proxy-revalidate` and `s-maxage` in a shared cache.
- An `Expires` at or before the response `Date`, or one that doesn't parse (such as `Expires: 0`), makes the response
  stale at once. Heuristic freshness and `DefaultMaxAge` don't extend it. An explicit `max-age` (or `s-maxage` in a
  shared cache) wins over `Expires`.
- A 304 updates the stored response's headers and whether it may be stored. If it changes to `no-store`, `Vary: *`, or
  `private` in a shared cache, the stored response is removed. This applies to background revalidation too. A 304
  without `Vary` keeps the original variant selection. If the updated response can't be stored, the old one is
  removed so its old rules are never used again; the caller still gets the response. If removing it fails too, the
  error reaches the caller.
- A `Cache-Control` or `Vary` field that doesn't parse (such as `private; max-age=600` or `max-age=-1`), also next to a valid one, is not
  read as absent: the response is not stored, because the header may have said `private` or `no-store`.
- An entry is kept at most a year, even when `Expires` is far in the future (`31 Dec 9999`).
- Weak ETags (`W/"abc"`) are sent back as weak validators.
- `only-if-cached` never contacts the origin, including for range requests, requests with their own validators,
  `no-store`, methods that aren't cached and background revalidation. When no stored response can be used, the
  answer is 504.

## Redirects, retries and request bodies

- A redirect the transport follows (`AllowAutoRedirect`, the default) answers a different URL, so it is not stored
  under the URL you requested. The transport also drops `Authorization` on a redirect, so storing it could hand one
  caller's response to another. Requests that end at the redirect target are cached under that URL as usual. If the
  target answers the cache's own validators with 304, the request is sent again without them, so you get the full response.
  A URL that was cached and now redirects has its stored response removed, also when background revalidation finds out.
- The cache stores responses for the request as you sent it, before the transport adds headers such as
  `Accept-Encoding` for automatic decompression. `Vary: Accept-Encoding` therefore works with `AutomaticDecompression`.
- The `If-None-Match` / `If-Modified-Since` headers the cache adds are removed again before the response is
  returned, so a retry handler (Polly, standard resilience) that sends the same message again gets a full response.
- A `GET` or `HEAD` with a body (search APIs such as Elasticsearch) goes to the origin: its URL doesn't identify it.
- The URL fragment (`#top`) is not part of the key; it never reaches the origin.

## Writes and invalidation

- A successful `POST`, `PUT`, `PATCH` or `DELETE` invalidates the cached responses for its URL before it returns: the
  URL you sent it to, also when the response redirects (`303 See Other`) elsewhere. A `GET` already in flight can't
  store its older response over it.
- With Redis, SQL Server or PostgreSQL configured, every instance sees the invalidation at once, even when backplane
  messages are lost.
- If the invalidation can't be confirmed, the call throws `HttpCacheInvalidationException`. The write may have
  succeeded at the origin, so don't retry it automatically. A `GET` that throws it is safe to retry.
- To invalidate from code, use `ICacheAdministrator`: one response by the id `ListEntries` gives, a client's
  responses by its tag (`http:` followed by `MethodCacheKeys.Part(clientName)`) or the tags in `HttpCacheOptions.Tags`,
  or everything with `ClearAsync`.

## Storage

Responses are stored in MethodCache's memory cache, in a budget of their own (`MaxHttpBytes`, 100 MiB by default,
headers included) next to method results. With Redis, SQL Server or PostgreSQL configured, they are stored there
instead, so every instance shares them. A response larger than the budget is streamed to the caller and not stored.
`AddHttpCaching` needs `AddMethodCache` in the same container, in either order. The admin endpoints and
`ICacheAdministrator` list and remove cached responses; they never show bodies, credentials, cookies or query strings.

## Transport and cookies

`AddHttpCaching()` turns off automatic cookies on the client's `SocketsHttpHandler` or `HttpClientHandler`, after all
other configuration of that handler, so the default registration caches at once. Other named clients are unaffected.
A handler that already has cookies off is left alone, so a cookie-free handler shared between clients keeps working.
`ConfigureHttpClientDefaults(b => b.AddHttpCaching())` caches every client, each under its own name: two clients
never share entries, and they all use the settings given there. A client has at most one cache: calling
`AddHttpCaching` again for one client, or twice, adds no second cache and applies every settings delegate. To give
one client other settings, use `services.Configure<HttpCacheOptions>("name", o => ...)`.
`KeepCookies=true` leaves the transport's cookies on and turns response caching, validation and stale fallback off for
that client. `only-if-cached` then returns 504, and a successful write still invalidates its URL.
Send caller cookies as request headers; a handler that keeps a hidden cookie jar belongs on a client without HTTP
caching.

The cache runs after every other handler you add, so caller identity and rewritten requests are visible before
lookup.

Other transports (wrappers the cache can't see into, native platform handlers), and handlers inside the primary
transport or below a cache handler you construct yourself, can't be checked. They bypass caching and log one warning
per named client, unless you set `AllowUnverifiedTransport`. Set it only when you have checked that the whole
transport sends no automatic cookies or hidden caller identity. A known transport with automatic cookies turned on
always bypasses.
