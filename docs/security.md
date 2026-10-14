# Security

What each package protects against, what it leaves to you, and the tests that hold it to that.

## Release publishing

`build/ci/publish.sh <tag>` retrieves only the configured BWS publishing secret after packing. The
NuGet API key goes to a .NET SDK 10.0.300+ CLI through `NUGET_API_KEY` and `NUGET_SYMBOL_API_KEY`, without appearing in command-line
arguments or shell traces. The BWS token is removed from the upload process's environment; inherited BWS
and both NuGet publishing credentials are removed from build-step environments. The script refuses a release
whose `MethodCache CI` status is not green. `build/ci/tests/test_publish.py` covers these boundaries and
failure paths. The host, release code and SDK remain trusted: this does not isolate malicious code running
under the same OS user from local credential files.

## MethodCache (keys, tags, invalidation)

| Threat | What MethodCache does |
|---|---|
| A caller crafts an argument (`"1:orders"`, `"a)"`, a newline) so that their call gets another call's key and its cached result | Keys separate arguments unambiguously: string parts escape `\` and `:`, `null` is `\0`, records are `(a:b)` with escaped members, and hashed keys length-prefix every value and record the runtime type of values in `object`, base-class or interface slots (`1` and `1L` in an `object[]` differ). JSON and MessagePack key-provider parts and nested strings preserve exact UTF-16 code units, including lone surrogates. Recursive key generation from providers or JSON getters cannot overwrite another invocation's key arguments. Arguments whose only state is private, including fields inherited from base classes, are rejected rather than hashed as an empty object. Framework types are written by their value (`BigInteger`, `IPAddress`, `JsonElement` and the others in [Cache keys](features/keys.md#framework-types)), not by public properties such as `BigInteger.Sign`, and any other type from .NET, ASP.NET Core or Microsoft.Extensions in a key, such as `StringBuilder`, is build error MCG018. Overloaded methods include the namespaces of their parameter types, so equally named types from different namespaces cannot share a method identity, and a method name that two interfaces of one hierarchy declare adds the parameter types (and the declaring interface for equal signatures) to the key, so `ILookup.Find(int)` and `ICatalog.Find(string)` cannot share an entry through the `ICatalog` decorator. Collection subclasses include their own public state alongside their elements in every key generator. No argument list produces another method's key or another argument list's key. Fluent versions have a separate namespace, with reserved user-key prefixes escaped, so a crafted raw key cannot reach a versioned entry. |
| An argument that is a secret ends up readable | Keys contain argument values in plain text (hashed keys are not encryption) and appear in the admin endpoints and, if you turn them on, in traces. Don't cache methods that take passwords or tokens; pass an id. |
| A tag built from user text invalidates someone else's entries | **Not prevented.** Tags are plain text: `"user:{userId}"` with the id `"1:orders"` is the same tag that `"user:{userId}:orders"` gives user 1, so invalidating one also removes the other. This only ever removes entries (they are loaded again); it never serves one caller's value to another. Use ids that can't contain your separator (`int`, `Guid`) in tag templates. |
| A load races invalidation and a newer write | Invalidated loads cannot publish over newer entries, even when custom weight calculation stalls or re-enters the cache. Existing callers may finish with their pre-invalidation result; later loads and writes retain their own entry. |
| A tag built from user text clears the whole distributed cache | L2 metadata preserves every UTF-16 code unit, including lone surrogates, so reading a tag cannot change its invalidation identity. A tag is only ever its own tag, whatever characters it holds; the internal "clear everything" marker can't be named by a tag. |
| `*` or `?` in user text widens an invalidation | `InvalidateByTagsAsync`, `InvalidateByKeysAsync` and the admin endpoints match exactly. Only `InvalidateByTagPatternAsync` reads wildcards: never build its pattern from user text. Patterns are matched without regular expressions, so a pattern can't cause catastrophic backtracking. |
| `[CacheKey(UseAsRawKey = true)]` | The argument *is* the key, unescaped, so a caller who controls it can read any other entry with that key. Use it only for keys your code builds. |

## Distributed cache (Redis, SQL Server)

| Threat | What MethodCache does |
|---|---|
| Corrupt, truncated or crafted bytes in the distributed cache | Read as a miss and loaded again: never an exception for the caller, never a `null` that wasn't stored, and lengths in the data are checked against its size before anything is allocated. |
| Stored data names a type to create (deserialization gadgets) | `JsonCacheSerializer` reads the method's declared type and ignores `$type`. `MessagePackCacheSerializer` uses the contractless resolver with `MessagePackSecurity.UntrustedData` and refuses typeless payloads. Keep it that way if you pass your own options. |
| Someone who can write to Redis or SQL Server | **Not prevented.** They can store any well-formed value, which every instance will serve. Custom method-result providers must implement backend-atomic invalidation stamps through `IAtomicInvalidationStore`; plain read-modify-write providers are rejected. The distributed cache is trusted: protect it like your database (authentication, TLS, network rules), and give each application its own `KeyPrefix`. Redis uses injective escaping before UTF-8 encoding, including for atomic stamps, so different UTF-16 keys cannot alias through replacement characters. SQL Server hashes exact UTF-16 cache keys so collation cannot alias distinct data or invalidation metadata keys, including NULs and trailing spaces. SQL Server also scopes backplane messages and cleanup by that exact prefix, independently of database collation. Automatic initialization rejects missing, nullable or incorrectly typed scope columns; it requires `binary(32) NOT NULL`. |
| Invalidations evicted from the store | A store that evicts a tag or removal stamp under memory pressure makes the entries it invalidated valid again (for example data cached before a permission change). Run Redis with `maxmemory-policy noeviction` or a Redis of its own; see [Keep invalidations from being evicted](features/distributed-cache.md#keep-invalidations-from-being-evicted). |
| Backplane messages | Carry keys and tags in lossless UTF-16 frames, so lone surrogates cannot change invalidation identity and newlines cannot forge another message type. Messages from another MethodCache version are rejected with one warning, without logging their contents. Anyone who can publish on the channel can invalidate entries, so the same trust applies. |

## MethodCache.AspNetCore (admin endpoints)

| Threat | What MethodCache does |
|---|---|
| An endpoint reachable without authorization | `MapMethodCacheAdmin` requires an authorization policy name, and every endpoint it maps requires that policy: anonymous callers get 401, signed-in users without it 403. A test reads the app's route table and checks every mapped endpoint, so a new endpoint without the policy fails a test. |
| A cross-site form or `fetch` without preflight calls `invalidate` with the admin's cookie | `POST invalidate` only accepts `application/json`, which a browser sends cross-site only after a CORS preflight; forms and `text/plain` get 415. The `DELETE` endpoints need a preflight too. Don't add a CORS policy that allows these routes from other origins. |
| Cached values leak through the admin API | Entries are listed with key, value type, age, time to live, tags and size, never the value. Keys contain argument values (see above), so the policy should admit only operators. |
| Bad input | A missing `key` or an out-of-range `limit` is 400, an empty or blank tag list is 400, and keys and tags are matched literally, never as patterns. |

The health check reports counts only. If you map it with a detailed response writer, put that endpoint behind
authorization too.

## MethodCache.HttpCaching

| Threat | What MethodCache does |
|---|---|
| Two users of one `HttpClient` get each other's responses (private cache, the default) | `Authorization` and `Cookie` are part of every response's key, including values that don't parse as a credential, so each caller only gets responses fetched with their own headers. `AddHttpCaching()` disables automatic cookies on known transports; explicitly supplied cookie headers isolate callers. Unknown transports, and handlers inside the primary transport, bypass caching unless explicitly verified and opted in with `AllowUnverifiedTransport`. A directly constructed cache handler also bypasses known transports with automatic cookies enabled. An empty header and no header are different callers too. Header values are individually length-framed, so a comma inside one value cannot alias two separate values. |
| A 200 or 304 changes response privacy or variant selection | Revalidated representations are checked again: `no-store`, `Vary: *` or shared-cache `private` removes previous responses. An omitted `Vary` on a 304 keeps its previous value. Unstorable full replacements also retire the old body, and selection-rule changes retire its URL epoch once so a newer concurrent publication remains reachable. |
| A shared cache (`IsSharedCache = true`) stores a personal response | Responses marked `private`, and responses to requests with any `Authorization` header, are not stored unless they say `public`, `s-maxage` or `must-revalidate` (RFC 9111 section 3.5). |
| Stale content bypasses required validation | Response or request `no-cache` requires successful validation even with stale-while-revalidate or stale-if-error enabled; origin failures propagate. |
| A cached response hands out a session | `Set-Cookie` is never stored. The caller the origin answered gets it, including on a `304`; nobody answered from the cache does. |
| Responses that vary by a header (tenant, language) get mixed | `Vary` and `KeyHeaders` keep one response per value. `Vary: *` is never served from the cache. |
| A write leaves another caller's stale copy, or serves one caller's copy to another after it | A successful `POST`, `PUT`, `PATCH` or `DELETE` changes the publication generation for every caller. Older in-flight responses write into an obsolete namespace. Shared generation records must be retained and protected from eviction; invalidation failures propagate to the caller. |
| Two URLs share an entry | The key is the method and the full URL, with reserved characters kept escaped (`%2F`, `%3F` and `/`, `?` are different URLs). |
| A followed redirect hands one caller's response to another | The transport drops `Authorization` on a redirect and changes the URL on the same message. A response that ended at another URL or method is never stored under the URL that was requested, and entries are keyed on the request as the caller sent it, before the transport changed it. |
| A malformed `Cache-Control` hides `private` or `no-store` | A `Cache-Control` or `Vary` field the parser rejects, also next to a valid one, makes the response not storable instead of being ignored. |
| Clients that get caching from `ConfigureHttpClientDefaults` share entries | Each client's handler gets its own name, so clients (often one per tenant or API key) never read each other's entries. |
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
| Span attributes | method id, hit or miss, TTL, group, version, number of keys invalidated; on failure the exception type, message and stack trace | `RecordCacheKeys` (a SHA-256 hash of the key, or the key itself with `HashCacheKeys = false`); `RecordCacheTags` (tags and invalidation patterns, which contain argument values when built from templates) |
| Span baggage | the correlation and invalidation ids you set | `ExportSensitiveData` adds the user and tenant ids |
| Metrics | dimensions `method`, `group`, `version`, `error.type`, and `client` and `outcome` for HTTP caching | none: tags, keys and URLs are never metric dimensions |

Cache spans copy the `http.*` and `url.*` attributes of the request span they belong to; turn off
`EnableHttpCorrelation` if those hold data your traces shouldn't. Exception messages come from your own code; keep
personal data out of them. Setting the user or tenant id to `null` or an empty string removes it, so a worker that
handles one message after another doesn't attribute a message without a user to the previous message's user.

## The tests

CI checkout collection and result reuse have a separate boundary from application caching. The collector only
removes coordinator-owned idle macOS clones with verified origin/full commit, terminal job results, and no
source changes or unexpected local data. It refuses redirected roots/results and keeps active checkouts and
unknown directories. Reuse requires the full SHA and all expected valid job exits; partial archives, failed
results and symlinked exit files cannot become a passing status. The regression suite under `build/ci/tests`
tests these protections. Build jobs still execute repository code as the build-server user; the collector is
not a sandbox for malicious pull requests.

Each row above is pinned by a test: `AdminEndpointSecurityTests` (ASP.NET Core), `HttpCacheIsolationTests`,
`HttpCacheEdgeCaseTests` and `UntrustedEntryTests` (HTTP caching), `TelemetryDefaultsLeakTests` and `TelemetryPrivacyTests` (OpenTelemetry), `KeyInjectionPropertyTests` and
`DistributedCacheSecurityTests` (keys, tags and the distributed cache).

MessagePack key identity includes derived state in nested base-class/interface slots and collections. `PolymorphicKeyReviewTests` checks that distinct derived argument data cannot share a cache key.

HTTP age arithmetic saturates during response correction and resident-time accumulation, so an extreme upstream Age value cannot overflow the freshness calculation. `ResponseDelayReviewTests` covers these input boundaries.

Bulk publication guards are tested during key, tag and clear invalidation; in-flight batch results cannot resurrect invalidated entries. Collection-state properties and the generator signature corpus cover tenant/scope state on collection subclasses.

Persistent SQL insert locks, including other scopes sharing the table, cannot hold back newer invalidations or other committed gaps. `LockedGapReviewTests` injects such locks through repeated polls and verifies late commit/rollback recovery.
