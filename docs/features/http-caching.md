# HTTP response caching

Add `AddHttpCaching()` to an `HttpClient` registration to cache HTTP responses according to their response
headers. Freshness comes from `Cache-Control` and expiry headers; ETag and Last-Modified enable conditional
validation. A successful 304 is returned to the caller as the full response.

Stale-while-revalidate and stale-if-error are optional. Response `no-cache` requires successful validation before
reusing the response, even when either option is enabled. Request `no-cache` has the same requirement: a 5xx
response or network error reaches the caller rather than falling back to cached content. `must-revalidate` also
forbids stale reuse, as do `proxy-revalidate` and `s-maxage` in shared caches.

A 304 response updates the stored representation's headers and storage policy. If it changes to `no-store`,
`Vary: *`, or `private` in a shared cache, the old response and variant index are removed. This applies to
background validation as well. An omitted `Vary` header preserves the original variant selection.

Successful unsafe requests rotate the cache publication generation before returning. An older GET or background
validation can complete, but publishes only into its old namespace, so later requests fetch the updated response.
The generation is shared through distributed storage when enabled. This also protects a cache write that was
already in progress when the POST finished; response writers never replace generation metadata.

Built-in storage uses 4096 generation slots per storage prefix to bound metadata. URLs that share a slot can cause
an additional cache miss when either is invalidated. Old response data expires normally; generation records use
maximum absolute expiration. If the backend evicts a generation record anyway, the next request starts a fresh
generation, so a lost record only causes cache misses and never brings back responses from before a POST. A
generation read error falls back to the origin. An invalidation write error is surfaced to the caller: the origin
write may already have succeeded.

Custom `IHttpCacheStorage` implementations supply `GetGenerationAsync` and `InvalidateAsync`. Every invalidation
must publish a new unique generation shared across storage instances. A generation value must never be reused,
and a missing or lost generation record must not map back to a namespace used before (start a new unique one
instead); grouping keys is allowed. Clearing memory also changes generations,
so a concurrent older reader cannot repopulate the active namespace.

**Migrating a custom storage:** a successful POST, PUT, PATCH or DELETE used to invalidate by calling `RemoveAsync`
on the cached GET. It now calls only `InvalidateAsync`, and responses are found through `GetGenerationAsync`. A
storage that returns a constant generation or implements `InvalidateAsync` as a no-op keeps serving old responses
after a write. A minimal correct storage:

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

A production storage also bounds its generation records (the built-in one groups URLs into 4096 slots).
Register it before `AddHttpCaching()`:

<!-- snippet: http-custom-storage-registration -->
```cs
builder.Services.AddSingleton<IHttpCacheStorage>(new DictionaryHttpCacheStorage(TimeProvider.System));
builder.Services.AddHttpClient<IGitHubClient, GitHubClient>()
    .AddHttpCaching();
```
<!-- endSnippet -->

With `UseDistributedCache`, HTTP storage prefers MethodCache's owned Redis/SQL Server cache. When no owned
cache is registered, it uses the application's `IDistributedCache`. It leaves application registrations unchanged.

A cacheable body with an accurate `Content-Length` of up to 1 MB is read directly into its final owned array.
Larger, unknown or inaccurate lengths use pooled temporary storage that grows only as bytes arrive, then copy into
one owned array; temporary and final storage can coexist during that copy. A declared length never allocates more
than 1 MB up front, and HEAD, 204 and 304 responses, which have no body, allocate nothing for their
`Content-Length`. Returned rentals are cleared. Only the size limit plus one byte is read
before an oversized unknown-length response is handed back as a stream, and disposing that response returns
its temporary storage even if the caller never reads it.

In short:

- A GET that was already in flight when a POST, PUT, PATCH or DELETE invalidated its URL never repopulates the
  cache with its older response.
- A `no-cache` response or request is never served stale without a successful validation, including with
  stale-while-revalidate or stale-if-error.
- A 304 whose headers forbid storage (`no-store`, `Vary: *`, or `private` in a shared cache) removes the stored
  response.
- Bodies are buffered with bounded reads and pooled temporary storage.

See the [HTTP package guide](../../src/MethodCache.HttpCaching/README.md) for registration, storage, private/shared
cache isolation, limits and metrics, and [security](../security.md) for the threat model.

## Behavior and compatibility

A failed response-body read fails the request rather than returning a partially consumed body. Buffered and streamed responses release their original content resources on completion, cancellation or disposal.

`only-if-cached` never contacts the origin, including range requests, caller validators, `no-store`, unsupported methods and background revalidation. An unavailable representation returns 504.

HTTP cache keys preserve individual header-value boundaries, including `Cookie` and custom tenant headers.

An explicit `Expires` at or before the response date is stale immediately; heuristic freshness and `DefaultMaxAge` do not extend it.

An invalid `Expires` date (including `Expires: 0`) is immediately stale, even when heuristic freshness or a default lifetime is enabled. Explicit `max-age` and `s-maxage` take precedence.


Unknown HTTP transports (including opaque wrappers and native platform handlers) bypass caching by default and emit one warning per named client. Set `AllowUnverifiedTransport` only when you have verified that the whole transport sends no automatic cookies or hidden caller identity. Recognized transports with automatic cookies always bypass.

`AddHttpCaching()` disables automatic cookies on the named client’s `SocketsHttpHandler` or `HttpClientHandler`, after all primary-handler configuration. The default registration therefore caches immediately. Send caller cookies explicitly; a handler that manages a hidden cookie jar belongs on a client without HTTP caching. Other named clients are unaffected.

Oversized HTTP responses remain streamed; disposing them closes both stream segments and the original content owner, even if a custom owner does not close its stream.

HTTP key framing changes all response cache keys, including requests without `Vary`; old entries become cold misses. See the [release compatibility notes](../developer/RELEASE_NOTES.md).

`AddHttpCaching()` runs the cache after all additional handlers, so injected caller identity and rewritten requests are visible before lookup. Delegating handlers inside the primary transport, or below a directly constructed cache handler, are unverified and bypass caching unless the entire chain is explicitly trusted.

HTTP cache freshness includes request/response delay when correcting the upstream Age header, for both initial responses and 304 validation. Age arithmetic saturates instead of overflowing on extreme headers.

Background HTTP revalidation preserves the request HTTP version, version policy and a shallow copy of its request options. Option values keep their original ownership; use values that remain valid for background work.
