[← MethodCache documentation](README.md)

# Security

What each package protects against, what it leaves to you, and the tests that hold it to that.

## MethodCache (keys, tags, invalidation)

| Threat | What MethodCache does |
|---|---|
| A caller crafts an argument (`"1:orders"`, `"a)"`, a newline) so that their call gets another call's key and its cached result | Keys separate arguments unambiguously: string parts escape `\` and `:`, `null` is `\0`, records are `(a:b)` with escaped members, and hashed keys length-prefix every value and record the runtime type of values in `object`, base-class or interface slots (`1` and `1L` in an `object[]` differ). JSON and MessagePack key-provider parts and nested strings preserve exact UTF-16 code units, including lone surrogates. Building a key that itself builds keys (an `ICacheKeyProvider`, a getter) can't change another call's key. Arguments whose only state is private, including fields inherited from base classes, are rejected rather than hashed as an empty object. Framework types are written by their value (`BigInteger`, `IPAddress`, `JsonElement` and the others in [Cache keys](features/keys.md#framework-types)), not by public properties such as `BigInteger.Sign`, and any other type from .NET, ASP.NET Core or Microsoft.Extensions in a key, such as `StringBuilder`, is build error MC0019. Overloaded methods include the namespaces of their parameter types, so equally named types from different namespaces cannot share a method identity, and a method name that two interfaces of one hierarchy declare adds the parameter types (and the declaring interface for equal signatures) to the key, so `ILookup.Find(int)` and `ICatalog.Find(string)` cannot share an entry through the `ICatalog` decorator. A collection subclass's own public properties are part of the key, with its elements. No argument list produces another method's key or another argument list's key. Fluent versions have a separate namespace, with reserved user-key prefixes escaped, so a crafted raw key cannot reach a versioned entry. |
| Two service libraries declare identical namespace/type names | Generated service keys and policy ids include the assembly simple name, without its version; keyed-service enum types, closed generic arguments and fallback runtime types (including nested object values) also include assembly identity. Assembly-name delimiters are escaped. `AssemblyKeySecurityTests` and the cross-assembly SignatureCorpus regression verify separate values and overrides. Libraries with the same assembly identity are trusted as the same logical library; this is not isolation between AssemblyLoadContexts. |
| An argument that is a secret ends up readable | Keys contain argument values in plain text (hashed keys are not encryption) and appear in the admin endpoints and, if you turn them on, in traces. Don't cache methods that take passwords or tokens; pass an id, or wrap a token that identifies the result (a URL with a SAS token, say) in a type whose `ICacheKeyProvider` returns a hash of it ([Secrets in arguments](features/keys.md#secrets-in-arguments)). |
| A dynamic tag argument impersonates another argument | Dynamic placeholders use canonical key parts: null has its own marker, strings escape colons and backslashes, enums use invariant numbers, and dates keep their full precision. Static template text stays literal. Tags remain an application-defined shared invalidation namespace; they are not an authorization boundary. |
| A load races invalidation and a newer write | Invalidated loads cannot publish over newer entries, even when custom weight calculation stalls or re-enters the cache. Existing callers may finish with their pre-invalidation result; later loads and writes retain their own entry. |
| A tag built from user text clears the whole distributed cache | The distributed cache stores every character of a tag exactly, lone surrogates included, so a tag never turns into another one. A tag is only ever its own tag, whatever characters it holds; the internal "clear everything" marker can't be named by a tag. |
| `*` or `?` in user text widens an invalidation | `InvalidateTagsAsync`, `InvalidateKeysAsync` and admin endpoints match exact keys and tags. Group invalidation uses an explicit common tag. |
| A tenant ID is missing from the cache key or invalidation scope | The console tenant example passes an explicit `Guid` tenant ID in the method arguments and tag, and `DemoTenantSecurityTests` verifies distinct results and selective invalidation. Applications must derive and authorize that tenant from the caller's identity. |

## Distributed cache (Redis, SQL Server, PostgreSQL)

| Threat | What MethodCache does |
|---|---|
| Corrupt, truncated or crafted bytes in the distributed cache | Read as a miss and loaded again: never an exception for the caller, never a `null` that wasn't stored, and lengths in the data are checked against its size before anything is allocated. |
| Stored data names a type to create (deserialization gadgets) | `JsonCacheSerializer` reads the method's declared type and ignores `$type`. `MessagePackCacheSerializer` uses the contractless resolver with `MessagePackSecurity.UntrustedData` and refuses typeless payloads. Keep it that way if you pass your own options. |
| Someone who can write to Redis, SQL Server or PostgreSQL | **Not prevented.** They can store any well-formed value, which every instance will serve. The distributed cache is trusted: protect it like your database (authentication, TLS, network rules), and give each application its own `KeyPrefix`. Different keys never share an entry: Redis escapes characters UTF-8 can't hold, and SQL Server hashes each exact key, so collation can't make two keys equal (casing, NULs, trailing spaces). SQL Server keeps each prefix's invalidations apart independently of collation too. PostgreSQL stores every key, and each prefix's backplane scope, as the SHA-256 of its exact UTF-16 code units, so NULs and lone surrogates (which PostgreSQL text can't hold) stay distinct. Its notifications carry only the sender's id; the invalidations themselves are read from the table. |
| Invalidations evicted from the store | A store that evicts a tag or removal stamp under memory pressure makes the entries it invalidated valid again (for example data cached before a permission change). Run Redis with `maxmemory-policy noeviction` or a Redis of its own; see [Keep invalidations from being evicted](features/distributed-cache.md#keep-invalidations-from-being-evicted). |
| Runtime overrides stored in the distributed cache | With a backplane, runtime overrides are records in the distributed cache that every instance applies, so someone who can write to it can also change any cached method's duration, tags or settings on every instance, or turn its caching off. An override never holds code: records carry only settings, the functions (`CacheWhen`, `DurationFor`, `TagsFor`, `SlidingFor`) are refused, and a record an instance can't read or apply is skipped with a warning. `SharedRuntimeOverridesTests` checks both. |
| Backplane messages | Carry keys and tags in lossless UTF-16 frames, so lone surrogates cannot change invalidation identity and newlines cannot forge another message type. Messages from another MethodCache version are rejected with one warning, without logging their contents. Anyone who can publish on the channel can invalidate entries, so the same trust applies. |

## MethodCache.AspNetCore (admin endpoints)

| Threat | What MethodCache does |
|---|---|
| An endpoint reachable without authorization | `MapMethodCacheAdmin` requires an authorization policy name, and every endpoint it maps requires that policy: anonymous callers get 401, signed-in users without it 403. A test reads the app's route table and checks every mapped endpoint, so a new endpoint without the policy fails a test. |
| The web demo is started outside Development | It requires cookie authentication and the `Admin` role, returning 401/403 for refused requests. Only Development permits anonymous admin access. `WebDemoSecurityTests` starts the actual sample and checks every admin route in both environments. The sample has no sign-in page; deployed applications must configure their own authentication. |
| Demo data can be reset outside Development | `/demo/reset` and `/demo/queries` are mapped only in Development. The sample tests verify that Production returns 404 for both. |
| A web demo price update omits a price or supplies null/negative input | The endpoint returns 400 before writing or invalidating. `WebDemoInputTests` verifies stored and cached products stay unchanged, and checks zero and the maximum decimal as valid boundaries. |
| A cross-site form or `fetch` without preflight calls `invalidate`, `invalidate-method` or `clear` with the admin's cookie | The `POST` endpoints only accept an `application/json` body, which a browser sends cross-site only after a CORS preflight; forms and `text/plain` get 415, and a request without a body 400. `DELETE entry` needs a preflight too. Don't add a CORS policy that allows these routes from other origins. |
| A filter in the wrong place empties the whole cache | Emptying everything is its own endpoint, `POST clear`, which takes only `{}`: a body with any property, such as `{ "tags": [...] }`, is 400. `invalidate` takes only `tags` and `invalidate-method` only `methodId`, and an unknown query parameter is 400 on every endpoint, so a misplaced filter is rejected instead of ignored. |
| `invalidate-method` removes another method's entries | The `methodId` must be exactly the id of a cached method, as `methods` lists it; anything else is 400 and removes nothing. Entries are matched by the method they belong to, not by a key prefix, so `GetUser` never reaches `GetUsers`. |
| An admin removes entries without a trace | Pass `audit` to `MapMethodCacheAdmin`: it gets the user, the action, its targets and the time for every change, before the change, and a throwing audit fails the request without changing anything. Reads and rejected requests aren't audited. Store the records where admins can't edit them. |
| Cached values leak through the admin API | Entries are listed with stored key, method id, value type, age, time to live, tags and size, never the value. Keys contain argument values (see above), so the policy should admit only operators. |
| Bad input | A missing `storedKey` or `methodId`, a `methodId` no cached method has, or an out-of-range `limit` is 400, an empty or blank tag list is 400, and keys, tags and method ids are matched literally, never as patterns. |

The health check reports counts only. If you map it with a detailed response writer, put that endpoint behind
authorization too.

## MethodCache.HttpCaching

| Threat | What MethodCache does |
|---|---|
| Two users of one `HttpClient` get each other's responses (private cache, the default) | `Authorization` and `Cookie` are part of every response's key, including values that don't parse as a credential, so each caller only gets responses fetched with their own headers. `AddHttpCaching()` disables automatic cookies on known transports after all handler configuration; `KeepCookies=true` preserves the transport and bypasses response caching, validation and stale fallback completely; explicitly supplied cookie headers isolate callers. Unknown transports, and handlers inside the primary transport, bypass caching unless explicitly verified and opted in with `AllowUnverifiedTransport`. A directly constructed cache handler also bypasses known transports with automatic cookies enabled. An empty header and no header are different callers too. Header values are individually length-framed as exact UTF-16, so a comma cannot alias multiple values and isolated surrogates cannot collapse through replacement encoding. |
| A 200 or 304 changes response privacy or variant selection | Revalidated representations are checked again: `no-store`, `Vary: *` or shared-cache `private` removes previous responses. An omitted `Vary` on a 304 keeps its previous value. A full response that can't be stored still removes the old one, and a change in which headers select a variant drops the URL's older responses without hiding a newer one. |
| A shared cache (`IsSharedCache = true`) stores a personal response | Responses marked `private`, and responses to requests with any `Authorization` header, are not stored unless they say `public`, `s-maxage` or `must-revalidate` (RFC 9111 section 3.5). |
| Stale content bypasses required validation | Response or request `no-cache` requires successful validation even with stale-while-revalidate or stale-if-error enabled; origin failures propagate. |
| A cached response hands out a session | `Set-Cookie` is never stored. The caller the origin answered gets it, including on a `304`; nobody answered from the cache does. |
| Responses that vary by a header (tenant, language) get mixed | `Vary` and `KeyHeaders` keep one response per value. `Vary: *` is never served from the cache. |
| A write leaves another caller's stale copy, or serves one caller's copy to another after it | A successful `POST`, `PUT`, `PATCH` or `DELETE` invalidates the URL's stored responses in the shared cache before it returns, also for clients with `KeepCookies`, and a response that was loading at the time is never stored after it. This doesn't depend on the backplane. When the invalidation can't be confirmed, the call throws `HttpCacheInvalidationException` and the cache keeps retrying; the request may have succeeded at the origin, so don't repeat it automatically. A `GET` or `HEAD` that throws it is safe to retry. |
| Two URLs share an entry | The key is the method and the full URL, with reserved characters kept escaped (`%2F`, `%3F` and `/`, `?` are different URLs). |
| A followed redirect hands one caller's response to another | The transport drops `Authorization` on a redirect and changes the URL on the same message. A response that ended at another URL or method is never stored under the URL that was requested, and entries are keyed on the request as the caller sent it, before the transport changed it. |
| A malformed `Cache-Control` hides `private` or `no-store` | A `Cache-Control` or `Vary` field the parser rejects, also next to a valid one, makes the response not storable instead of being ignored. |
| Clients that get caching from `ConfigureHttpClientDefaults` share entries | Each client's handler gets its own name, so clients (often one per tenant or API key) never read each other's entries. |
| Lost or replayed HTTP invalidation records | Invalidation records only move forward; a lost one only causes misses, and an older response never comes back. Two URLs that share a record can cause an extra miss, never a response for the wrong caller. |
| Administration becomes a body or credential leak, or a false global inventory | Administration lists only what is in this instance's memory, plus at most 1024 recently seen distributed entries without their bodies. Ids are hashes: no query string, credentials or cookies. The distributed list doesn't claim the entries still exist. An unknown id is not found. |
| An oversized response exhausts the local HTTP budget | Method and HTTP budgets evict independently. Body reads are bounded by response limits and remaining conservative HTTP cost; oversized responses remain available as streams and are not cached. |
| Crafted entries in the distributed HTTP cache | Read as a miss, with every length checked against the data. As above, whoever can write that cache can store any response. |

Not guaranteed, by design:

- In a **shared** cache, a response stored for an anonymous request is served to authorized requests for the same URL
  too, and responses that depend on cookies are shared unless the origin sends `Vary: Cookie` or `private`. That is
  what RFC 9111 specifies for a shared cache. Add `Cookie` or `Authorization` to `KeyHeaders` when your origin
  personalises without saying so.
- Anonymous callers of a private cache are one caller: they share responses.
- HTTP key hashing preserves complete UTF-16 header frames; temporary pooled buffers are cleared before reuse.
- HTTP cache diagnostics omit raw URLs, headers and exception payloads. Static configured client names remain visible.
  Application/transport instrumentation has its own logging configuration.

## MethodCache.OpenTelemetry

Core owns the `MethodCache` Meter/ActivitySource. The OpenTelemetry package only subscribes to them.

| Signal | Recorded by default | Opt-in |
|---|---|---|
| Spans | `methodcache.kind`, the static `methodcache.method` or `methodcache.http.client`, `methodcache.operation` or `methodcache.invalidation.type`, and `error.type`; no exception messages, stacks or copied HTTP/URL attributes | Core `MethodCacheTelemetryOptions.RecordCacheKeys` (SHA-256 by default, raw with `HashCacheKeys=false`); `RecordCacheTags` for application tags |
| Baggage | No library-added baggage | The app controls normal ambient .NET baggage propagation/export |
| Metrics | `methodcache.kind`, the static `methodcache.method` or `methodcache.http.client`, `methodcache.bulk`, `methodcache.result`, `methodcache.invalidation.type` and `.stage`, `methodcache.operation` and `error.type` | None: keys, tags, URLs, user ids and tenant ids never become metric dimensions |

Hashing creates stable identifiers and does not make keys anonymous. Leave identity recording disabled unless the
export destination is appropriate. Core, Redis, SQL and EF Core diagnostic logs omit backend exception text because it
can contain credentials, keys or argument values, and a failed invalidation after an EF Core commit logs the number of
tags, not the tags. An invalid configuration reload logs the path of the setting it
rejected, never the value, and failed policy watchers log only the error type; both keep the last accepted policies. Health check failures expose exception types only. Standard
application activity parentage and baggage remain intact, including when origin code uses its ambient authorization
context; MethodCache neither adds identity baggage nor strips the application's context.

## The tests

Each row above is pinned by a test: `AdminEndpointSecurityTests` (ASP.NET Core), `HttpCacheIsolationTests`,
`HttpCacheEdgeCaseTests` and `UntrustedEntryTests` (HTTP caching), `CoreInstrumentationTests` (OpenTelemetry SDK) and `TelemetryPrivacyTests` (Core), `KeyInjectionPropertyTests`,
`DistributedCacheSecurityTests` and `SharedRuntimeOverridesTests` (keys, tags and the distributed cache), and
`InvalidationLoggingTests` (EF Core). The implementation-level guarantees behind these rows, and the CI and release boundaries, are in
[security internals](developer/security-internals.md).
