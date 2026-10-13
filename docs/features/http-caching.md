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
  without `Vary` keeps the original variant selection.
- `only-if-cached` never contacts the origin, including for range requests, requests with their own validators,
  `no-store`, methods that aren't cached and background revalidation. When no stored response can be used, the
  answer is 504.

## Writes and invalidation

A successful `POST`, `PUT`, `PATCH` or `DELETE` starts a new cache generation for its URL before it returns. A GET or
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
Send caller cookies as request headers; a handler that keeps a hidden cookie jar belongs on a client without HTTP
caching.

The cache runs after every other handler you add, so caller identity and rewritten requests are visible before
lookup.

Other transports (wrappers the cache can't see into, native platform handlers), and handlers inside the primary
transport or below a cache handler you construct yourself, can't be checked. They bypass caching and log one warning
per named client, unless you set `AllowUnverifiedTransport`. Set it only when you have checked that the whole
transport sends no automatic cookies or hidden caller identity. A known transport with automatic cookies turned on
always bypasses.
