# HTTP response caching

Add `AddHttpCaching()` to an `HttpClient` registration to cache HTTP responses according to their response headers,
as RFC 9111 describes. Freshness comes from `Cache-Control` and expiry headers; `ETag` and `Last-Modified` enable
conditional validation, and a successful 304 is returned to the caller as the full response.

<!-- snippet: http-basic -->
```cs
builder.Services.AddHttpClient<IGitHubClient, GitHubClient>()
    .AddHttpCaching();
```
<!-- endSnippet -->

The [package guide](../../src/MethodCache.HttpCaching/README.md) covers settings, storage, private and shared caches,
limits and metrics. This page covers the rules the cache follows and how to write your own storage. See
[Security](../security.md#methodcachehttpcaching) for the threat model.

A full replacement response also retires the previous cached representation when it cannot be stored, including `no-store`, shared-cache `private`, a body over the size limit, or a failed replacement write. The next caller cannot fall back to an older fresh body. Stale-while-revalidate respects explicit request `max-age`, `min-fresh` and `max-stale` limits.

Concurrent writers with the same variant selection share its namespace, including across processes using distributed storage. The body-free variant index is retained for the maximum supported response lifetime (365 days); individual variants still expire at their own TTL. A change to the selecting headers retires the URL publication epoch exactly once; a concurrent response published after that retirement remains reachable; the current response reaches its caller, and the next request fetches and stores the new schema. If deleting the old entry fails after the epoch was rotated, foreground validation reports that error without rotating again; background validation logs it. A concurrent publication in the new epoch remains reachable.

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

A successful `POST`, `PUT`, `PATCH` or `DELETE` starts a new cache generation for its URL before it returns. That is
the URL you sent it to, also when the response redirects (`303 See Other`) elsewhere. A GET or
background revalidation that was already in flight can still complete, but it stores its response under the old
generation, so later requests fetch the updated response. With `UseDistributedCache` the generation is shared by
every instance.

The built-in storage groups URLs into 4096 generation slots per storage prefix, so its metadata stays bounded. Two
URLs that share a slot can cause an extra cache miss when either is invalidated. Generation records never expire on
their own; if the backend evicts one anyway, the next request starts a new generation, so a lost record only causes
cache misses and never brings back a response from before a write. If reading the generation fails, the request goes
to the origin. If storing a new generation fails, the error reaches the caller, although the write to the origin may
already have succeeded.

## Custom storage

To store responses somewhere else, implement `IHttpCacheStorage`. A successful write calls only `InvalidateAsync`,
and responses are looked up under the generation from `GetGenerationAsync`, so both must be correct:

- Every invalidation publishes a new generation, unique and shared by every storage instance. A storage that returns
  a constant generation, or implements `InvalidateAsync` as a no-op, keeps serving old responses after a write.
- A generation value is never reused. A missing or lost generation record starts a new unique one, never one used
  before. Several keys may share a generation.
- Clearing the storage also changes generations, so a request still in flight can't store an older response into
  the active generation.

A minimal correct storage:

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

A production storage also bounds its generation records (the built-in one groups URLs into 4096 slots). Register it
before `AddHttpCaching()`:

<!-- snippet: http-custom-storage-registration -->
```cs
builder.Services.AddSingleton<IHttpCacheStorage>(new DictionaryHttpCacheStorage(TimeProvider.System));
builder.Services.AddHttpClient<IGitHubClient, GitHubClient>()
    .AddHttpCaching();
```
<!-- endSnippet -->

## Transport and cookies

`AddHttpCaching()` turns off automatic cookies on the client's `SocketsHttpHandler` or `HttpClientHandler`, after all
other configuration of that handler, so the default registration caches at once. Other named clients are unaffected.
A handler that already has cookies off is left alone, so a cookie-free handler shared between clients keeps working.
`ConfigureHttpClientDefaults(b => b.AddHttpCaching())` caches every client, each under its own name: two clients
never share entries, and they all use the settings given there.
Send caller cookies as request headers; a handler that keeps a hidden cookie jar belongs on a client without HTTP
caching.

The cache runs after every other handler you add, so caller identity and rewritten requests are visible before
lookup.

Other transports (wrappers the cache can't see into, native platform handlers), and handlers inside the primary
transport or below a cache handler you construct yourself, can't be checked. They bypass caching and log one warning
per named client, unless you set `AllowUnverifiedTransport`. Set it only when you have checked that the whole
transport sends no automatic cookies or hidden caller identity. A known transport with automatic cookies turned on
always bypasses.

HTTP cache freshness includes request/response delay when correcting the upstream Age header, for both initial responses and 304 validation. Age arithmetic saturates instead of overflowing on extreme headers.

Background HTTP revalidation preserves the request HTTP version, version policy and a shallow copy of its request options. Option values keep their original ownership; use values that remain valid for background work.
