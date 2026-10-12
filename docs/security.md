# Security

What each package protects against, what it leaves to you, and the tests that hold it to that.

## MethodCache (keys, tags, invalidation)

| Threat | What MethodCache does |
|---|---|
| A caller crafts an argument (`"1:orders"`, `"a)"`, a newline) so that their call gets another call's key and its cached result | Keys separate arguments unambiguously: string parts escape `\` and `:`, `null` is `\0`, records are `(a:b)` with escaped members, and hashed keys length-prefix every value and record the runtime type of values in `object`, base-class or interface slots (`1` and `1L` in an `object[]` differ). Recursive key generation from providers or JSON getters cannot overwrite another invocation's key arguments. Arguments whose only state is private, including fields inherited from base classes, are rejected rather than hashed as an empty object. Overloaded methods include the namespaces of their parameter types, so equally named types from different namespaces cannot share a method identity. No argument list produces another method's key or another argument list's key. |
| An argument that is a secret ends up readable | Keys contain argument values in plain text (hashed keys are not encryption) and appear in the admin endpoints and, if you turn them on, in traces. Don't cache methods that take passwords or tokens; pass an id. |
| A tag built from user text invalidates someone else's entries | **Not prevented.** Tags are plain text: `"user:{userId}"` with the id `"1:orders"` is the same tag that `"user:{userId}:orders"` gives user 1, so invalidating one also removes the other. This only ever removes entries (they are loaded again); it never serves one caller's value to another. Use ids that can't contain your separator (`int`, `Guid`) in tag templates. |
| A tag built from user text clears the whole distributed cache | A tag is only ever its own tag, whatever characters it holds; the internal "clear everything" marker can't be named by a tag. |
| `*` or `?` in user text widens an invalidation | `InvalidateByTagsAsync`, `InvalidateByKeysAsync` and the admin endpoints match exactly. Only `InvalidateByTagPatternAsync` reads wildcards: never build its pattern from user text. Patterns are matched without regular expressions, so a pattern can't cause catastrophic backtracking. |
| `[CacheKey(UseAsRawKey = true)]` | The argument *is* the key, unescaped, so a caller who controls it can read any other entry with that key. Use it only for keys your code builds. |
| A tenant ID is missing from the cache key or invalidation scope | The console tenant example passes an explicit `Guid` tenant ID in the method arguments and tag, and `DemoTenantSecurityTests` verifies distinct results and selective invalidation. Applications must derive and authorize that tenant from the caller's identity. |

## Distributed cache (Redis, SQL Server)

| Threat | What MethodCache does |
|---|---|
| Corrupt, truncated or crafted bytes in the distributed cache | Read as a miss and loaded again: never an exception for the caller, never a `null` that wasn't stored, and lengths in the data are checked against its size before anything is allocated. |
| Stored data names a type to create (deserialization gadgets) | `JsonCacheSerializer` reads the method's declared type and ignores `$type`. `MessagePackCacheSerializer` uses the contractless resolver with `MessagePackSecurity.UntrustedData` and refuses typeless payloads. Keep it that way if you pass your own options. |
| Someone who can write to Redis or SQL Server | **Not prevented.** They can store any well-formed value, which every instance will serve. Custom method-result providers must implement backend-atomic invalidation stamps through `IAtomicInvalidationStore`; plain read-modify-write providers are rejected. The distributed cache is trusted: protect it like your database (authentication, TLS, network rules), and give each application its own `KeyPrefix`. SQL Server hashes exact UTF-16 cache keys so collation cannot alias distinct data or invalidation metadata keys, including NULs and trailing spaces. SQL Server also scopes backplane messages and cleanup by that exact prefix, independently of database collation. |
| Backplane messages | Carry keys and tags as the last field, so a key with newlines can't forge another message type. Anyone who can publish on the channel can invalidate entries, so the same trust applies. |

## MethodCache.AspNetCore (admin endpoints)

| Threat | What MethodCache does |
|---|---|
| An endpoint reachable without authorization | `MapMethodCacheAdmin` requires an authorization policy name, and every endpoint it maps requires that policy: anonymous callers get 401, signed-in users without it 403. A test reads the app's route table and checks every mapped endpoint, so a new endpoint without the policy fails a test. |
| The web demo is started outside Development | It requires cookie authentication and the `Admin` role, returning 401/403 for refused requests. Only Development permits anonymous admin access. `WebDemoSecurityTests` starts the actual sample and checks every admin route in both environments. The sample has no sign-in page; deployed applications must configure their own authentication. |
| Demo data can be reset outside Development | `/demo/reset` and `/demo/queries` are mapped only in Development. The sample tests verify that Production returns 404 for both. |
| A web demo price update omits a price or supplies null/negative input | The endpoint returns 400 before writing or invalidating. `WebDemoInputTests` verifies stored and cached products stay unchanged, and checks zero and the maximum decimal as valid boundaries. |
| A cross-site form or `fetch` without preflight calls `invalidate` with the admin's cookie | `POST invalidate` only accepts `application/json`, which a browser sends cross-site only after a CORS preflight; forms and `text/plain` get 415. The `DELETE` endpoints need a preflight too. Don't add a CORS policy that allows these routes from other origins. |
| Cached values leak through the admin API | Entries are listed with key, value type, age, time to live, tags and size, never the value. Keys contain argument values (see above), so the policy should admit only operators. |
| Bad input | A missing `key` or an out-of-range `limit` is 400, an empty or blank tag list is 400, and keys and tags are matched literally, never as patterns. |

The health check reports counts only. If you map it with a detailed response writer, put that endpoint behind
authorization too.

## MethodCache.HttpCaching

| Threat | What MethodCache does |
|---|---|
| Two users of one `HttpClient` get each other's responses (private cache, the default) | `Authorization` and `Cookie` are part of every response's key, including values that don't parse as a credential, so each caller only gets responses fetched with their own headers. An empty header and no header are different callers too. |
| A 304 changes response privacy or variant selection | Revalidated representations are checked again: `no-store`, `Vary: *` or shared-cache `private` removes previous responses. An omitted `Vary` keeps its previous value. |
| A shared cache (`IsSharedCache = true`) stores a personal response | Responses marked `private`, and responses to requests with any `Authorization` header, are not stored unless they say `public`, `s-maxage` or `must-revalidate` (RFC 9111 section 3.5). |
| Stale content bypasses required validation | Response or request `no-cache` requires successful validation even with stale-while-revalidate or stale-if-error enabled; origin failures propagate. |
| A cached response hands out a session | `Set-Cookie` is never stored. The caller the origin answered gets it, including on a `304`; nobody answered from the cache does. |
| Responses that vary by a header (tenant, language) get mixed | `Vary` and `KeyHeaders` keep one response per value. `Vary: *` is never served from the cache. |
| A write leaves another caller's stale copy, or serves one caller's copy to another after it | A successful `POST`, `PUT`, `PATCH` or `DELETE` changes the publication generation for every caller. Older in-flight responses write into an obsolete namespace. Shared generation records must be retained and protected from eviction; invalidation failures propagate to the caller. |
| Two URLs share an entry | The key is the method and the full URL, with reserved characters kept escaped (`%2F`, `%3F` and `/`, `?` are different URLs). |
| Crafted entries in the distributed HTTP cache | Read as a miss, with every length checked against the data. As above, whoever can write that cache can store any response. |

Not guaranteed, by design:

- In a **shared** cache, a response stored for an anonymous request is served to authorized requests for the same URL
  too, and responses that depend on cookies are shared unless the origin sends `Vary: Cookie` or `private`. That is
  what RFC 9111 specifies for a shared cache. Add `Cookie` or `Authorization` to `KeyHeaders` when your origin
  personalises without saying so.
- Anonymous callers of a private cache are one caller: they share responses.
- Debug and warning logs include request URLs. Don't put secrets in query strings.

## MethodCache.OpenTelemetry

With the default options, nothing that identifies a person or an argument leaves the process:

| Signal | Recorded by default | Opt-in |
|---|---|---|
| Span attributes | method name, hit or miss, TTL, group, version, number of keys invalidated; on failure the exception type, message and stack trace | `RecordCacheKeys` (a SHA-256 hash of the key, or the key itself with `HashCacheKeys = false`); `RecordCacheTags` (tags and invalidation patterns, which contain argument values when built from templates) |
| Span baggage | the correlation and invalidation ids you set | `ExportSensitiveData` adds the user and tenant ids |
| Metrics | dimensions `method`, `group`, `version`, `error.type`, and `client` and `outcome` for HTTP caching | none: tags, keys and URLs are never metric dimensions |

Cache spans copy the `http.*` and `url.*` attributes of the request span they belong to; turn off
`EnableHttpCorrelation` if those hold data your traces shouldn't. Exception messages come from your own code; keep
personal data out of them.

## The tests

Each row above is pinned by a test: `AdminEndpointSecurityTests` (ASP.NET Core), `HttpCacheIsolationTests` and
`UntrustedEntryTests` (HTTP caching), `TelemetryDefaultsLeakTests` (OpenTelemetry), `KeyInjectionPropertyTests` and
`DistributedCacheSecurityTests` (keys, tags and the distributed cache).
