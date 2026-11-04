[← MethodCache documentation](../README.md)

# HTTP caching reference

The exact behavior of `MethodCache.HttpCaching`, for advanced users and coding agents. Start with
[HTTP response caching](../features/http-caching.md).

## Variants

- Responses are selected by the `Vary` header fields and the configured `KeyHeaders`, compared case-insensitively.
  A private cache always keeps `Authorization` and `Cookie` apart.
- Every variant of a URL is kept for at most 365 days; each expires at its own lifetime.
- When a response changes which headers select its variants, the URL's earlier variants are no longer used, once. The
  current response still reaches its caller, and the next request stores the new variant. If removing the old entry
  fails, a foreground request reports the error, and background revalidation logs it.

## Replacing a stored response

- A full response replaces the stored one. When it can't be stored (`no-store`, `private` in a shared cache, a body
  over the size limit, or a failed write), the old one is removed, so the next caller never falls back to an older
  body.
- Stale-while-revalidate respects the request's `max-age`, `min-fresh` and `max-stale`.
- The `Age` header is corrected by the request and response delay, for first responses and for 304s, and very large
  values can't overflow.
- Background revalidation sends the request with its HTTP version, version policy and a shallow copy of its request
  options. Use option values that stay valid after the original request has finished.

## Invalidation details

- Invalidating a URL starts a new generation for it; responses stored under an older generation are no longer read.
  With Redis, SQL Server or PostgreSQL the generations are stored there, so they don't depend on the backplane.
- URLs share 4096 generation slots per `KeyPrefix`, so the stored data stays bounded. Two URLs in one slot can cause an
  extra miss when either is invalidated. A lost generation record only causes misses, never an older response.
- If reading the generation fails, the request goes to the origin.
- Cancelling the caller stops its wait; the cache finishes the invalidation. Disposing the service provider
  asynchronously waits for pending invalidations and retries queued ones once. Any it can't confirm are logged as a
  count; other instances may then serve old responses until they expire.
- Invalidating the client's own tag scans the responses in memory, so its cost grows with their number. URL and
  configured tags use indexes.

## Administration

- `ICacheAdministrator` lists the responses in memory, or with a distributed cache up to 1024 responses this instance
  has seen there (`Storage = ObservedDistributed`): not proof that they are still stored, nor a list of everything
  stored. Ids are hashes; an unknown id returns NotFound.
- Removing an id removes every variant of its URL. `ClearAsync` removes every response.
- HTTP administration and byte statistics also work with `NoOpCacheManager` for methods.
- A client's tag is `http:` followed by `MethodCacheKeys.Part(clientName)` for names up to 128 UTF-16 code units;
  longer names use `http:#:` followed by the SHA-256 of the name, and administration shows the start of the name and
  its hash. `HttpCacheOptions.Tags` takes at most 125 non-blank tags of at most 1024 code units; other values fail at
  startup.
- HTTP entries have their own format: method serializers, durations, jitter and fail-safe settings don't apply to them.
