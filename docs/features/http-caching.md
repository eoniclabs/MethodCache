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
maximum absolute expiration and must be protected from backend eviction. A generation read error falls back to
the origin. An invalidation write error is surfaced to the caller: the origin write may already have succeeded.

Custom `IHttpCacheStorage` implementations supply `GetGenerationAsync` and `InvalidateAsync`. Every invalidation
must publish a new unique generation shared across storage instances. Generations cannot expire or be reused
while old responses or writers may exist; grouping keys is allowed. Clearing memory also changes generations,
so a concurrent older reader cannot repopulate the active namespace.

With `UseDistributedCache`, HTTP storage prefers MethodCache's owned Redis/SQL Server cache. When no owned
cache is registered, it uses the application's `IDistributedCache`. It leaves application registrations unchanged.

See the [HTTP package guide](../../src/MethodCache.HttpCaching/README.md) for registration, storage, private/shared
cache isolation, limits and metrics, and [security](../security.md) for the threat model.
