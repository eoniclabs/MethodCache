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
| A caller crafts an argument (`"1:orders"`, `"a)"`, a newline) so that their call gets another call's key and its cached result | Keys separate arguments unambiguously: string parts escape `\` and `:`, `null` is `\0`, records are `(a:b)` with escaped members, and hashed keys length-prefix every value and record the runtime type of values in `object`, base-class or interface slots (`1` and `1L` in an `object[]` differ). JSON and MessagePack key-provider parts and nested strings preserve exact UTF-16 code units, including lone surrogates. Recursive key generation from providers or JSON getters cannot overwrite another invocation's key arguments. Arguments whose only state is private, including fields inherited from base classes, are rejected rather than hashed as an empty object. Framework types are written by their value (`BigInteger`, `IPAddress`, `JsonElement` and the others in [Cache keys](features/keys.md#framework-types)), not by public properties such as `BigInteger.Sign`, and any other type from .NET, ASP.NET Core or Microsoft.Extensions in a key, such as `StringBuilder`, is build error MC0019. Overloaded methods include the namespaces of their parameter types, so equally named types from different namespaces cannot share a method identity, and a method name that two interfaces of one hierarchy declare adds the parameter types (and the declaring interface for equal signatures) to the key, so `ILookup.Find(int)` and `ICatalog.Find(string)` cannot share an entry through the `ICatalog` decorator. Collection subclasses include their own public state alongside their elements in every key generator. No argument list produces another method's key or another argument list's key. Fluent versions have a separate namespace, with reserved user-key prefixes escaped, so a crafted raw key cannot reach a versioned entry. |
| An argument that is a secret ends up readable | Keys contain argument values in plain text (hashed keys are not encryption) and appear in the admin endpoints and, if you turn them on, in traces. Don't cache methods that take passwords or tokens; pass an id. |
| A dynamic tag argument impersonates another argument | Dynamic placeholders use canonical key parts: null has its own marker, strings escape colons and backslashes, enums use invariant numbers, and dates keep their full precision. Static template text stays literal. Tags remain an application-defined shared invalidation namespace; they are not an authorization boundary. |
| A load races invalidation and a newer write | Invalidated loads cannot publish over newer entries, even when custom weight calculation stalls or re-enters the cache. Existing callers may finish with their pre-invalidation result; later loads and writes retain their own entry. |
| A tag built from user text clears the whole distributed cache | L2 metadata preserves every UTF-16 code unit, including lone surrogates, so reading a tag cannot change its invalidation identity. A tag is only ever its own tag, whatever characters it holds; the internal "clear everything" marker can't be named by a tag. |
| `*` or `?` in user text widens an invalidation | `InvalidateTagsAsync`, `InvalidateKeysAsync` and admin endpoints match exact keys and tags. Group invalidation uses an explicit common tag. |

## Distributed cache (Redis, SQL Server)

| Threat | What MethodCache does |
|---|---|
| Corrupt, truncated or crafted bytes in the distributed cache | Read as a miss and loaded again: never an exception for the caller, never a `null` that wasn't stored, and lengths in the data are checked against its size before anything is allocated. |
| Stored data names a type to create (deserialization gadgets) | `JsonCacheSerializer` reads the method's declared type and ignores `$type`. `MessagePackCacheSerializer` uses the contractless resolver with `MessagePackSecurity.UntrustedData` and refuses typeless payloads. Keep it that way if you pass your own options. |
| Someone who can write to Redis or SQL Server | **Not prevented.** They can store any well-formed value, which every instance will serve. Owned Redis and SQL Server providers use backend-atomic maximum stamps and permanent epoch updates; their internal contracts are not application extension points. The distributed cache is trusted: protect it like your database (authentication, TLS, network rules), and give each application its own `KeyPrefix`. Redis uses injective escaping before UTF-8 encoding, including for atomic stamps, so different UTF-16 keys cannot alias through replacement characters. SQL Server hashes exact UTF-16 cache keys so collation cannot alias distinct data or invalidation metadata keys, including NULs and trailing spaces. SQL Server also scopes backplane messages and cleanup by that exact prefix, independently of database collation. Automatic initialization rejects missing, nullable or incorrectly typed scope columns; it requires `binary(32) NOT NULL`. |
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
| Two users of one `HttpClient` get each other's responses (private cache, the default) | `Authorization` and `Cookie` are part of every response's key, including values that don't parse as a credential, so each caller only gets responses fetched with their own headers. `AddHttpCaching()` disables automatic cookies on known transports after all handler configuration; `KeepCookies=true` preserves the transport and bypasses response caching, validation and stale fallback completely; explicitly supplied cookie headers isolate callers. Unknown transports, and handlers inside the primary transport, bypass caching unless explicitly verified and opted in with `AllowUnverifiedTransport`. A directly constructed cache handler also bypasses known transports with automatic cookies enabled. An empty header and no header are different callers too. Header values are individually length-framed as exact UTF-16, so a comma cannot alias multiple values and isolated surrogates cannot collapse through replacement encoding. |
| A 200 or 304 changes response privacy or variant selection | Revalidated representations are checked again: `no-store`, `Vary: *` or shared-cache `private` removes previous responses. An omitted `Vary` on a 304 keeps its previous value. Unstorable full replacements also retire the old body, and selection-rule changes retire its URL epoch once so a newer concurrent publication remains reachable, including when old-entry deletion fails after a successful rotation. |
| A shared cache (`IsSharedCache = true`) stores a personal response | Responses marked `private`, and responses to requests with any `Authorization` header, are not stored unless they say `public`, `s-maxage` or `must-revalidate` (RFC 9111 section 3.5). |
| Stale content bypasses required validation | Response or request `no-cache` requires successful validation even with stale-while-revalidate or stale-if-error enabled; origin failures propagate. |
| A cached response hands out a session | `Set-Cookie` is never stored. The caller the origin answered gets it, including on a `304`; nobody answered from the cache does. |
| Responses that vary by a header (tenant, language) get mixed | `Vary` and `KeyHeaders` keep one response per value. `Vary: *` is never served from the cache. |
| A write leaves another caller's stale copy, or serves one caller's copy to another after it | A confirmed `POST`, `PUT`, `PATCH` or `DELETE` atomically advances every relevant shared URL generation before returning, including for bypass/KeepCookies clients. Older origin/304/body writes remain in an obsolete namespace. L2 nodes capture fresh generations in one bounded metadata batch; correctness does not rely on backplane delivery. An unconfirmed mutation throws `HttpCacheInvalidationException`, disposes its response and retains owned recovery after caller cancellation. The origin may have succeeded: do not automatically repeat it. |
| Two URLs share an entry | The key is the method and the full URL, with reserved characters kept escaped (`%2F`, `%3F` and `/`, `?` are different URLs). |
| A followed redirect hands one caller's response to another | The transport drops `Authorization` on a redirect and changes the URL on the same message. A response that ended at another URL or method is never stored under the URL that was requested, and entries are keyed on the request as the caller sent it, before the transport changed it. |
| A malformed `Cache-Control` hides `private` or `no-store` | A `Cache-Control` or `Vary` field the parser rejects, also next to a valid one, makes the response not storable instead of being ignored. |
| Clients that get caching from `ConfigureHttpClientDefaults` share entries | Each client's handler gets its own name, so clients (often one per tenant or API key) never read each other's entries. |
| Lost or replayed HTTP generation metadata | Native atomic revisions only advance; missing/overflowed metadata gets a fresh initialization id. Old values never regain their namespace. Global/client/static-tag components are shared with Core-only tag/clear writes, including across value formats. URL-slot collisions cause extra misses, never shared caller identity. The local reverse index follows the same bounded slots and removes every retired body from an invalidated slot. Native observations keep their separate per-URL index. |
| Administration becomes a body or credential leak, or a false global inventory | Core exposes only resident metadata or at most 1024 historical, expiring, body-free L2 observations. Ids are bounded hashes; no raw query, credentials or cookie headers enter the observations. `ObservedDistributed` explicitly does not confirm presence or global inventory. Unknown ids are NotFound. |
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
| Spans | Static method/client names, operation/type and `error.type`; no exception messages, stacks or copied HTTP/URL attributes | Core `MethodCacheTelemetryOptions.RecordCacheKeys` (SHA-256 by default, raw with `HashCacheKeys=false`); `RecordCacheTags` for application tags |
| Baggage | No library-added baggage | The app controls normal ambient .NET baggage propagation/export |
| Metrics | `kind`, static `name`, `result`, invalidation `type`/`stage`, transport `operation` and `error.type` | None: keys, tags, URLs, user ids and tenant ids never become metric dimensions |

Hashing creates stable identifiers and does not make keys anonymous. Leave identity recording disabled unless the
export destination is appropriate. Core, Redis and SQL diagnostic logs omit backend exception text because it can
contain credentials, keys or argument values. Invalid configuration reloads and failed policy watchers likewise log
only the error type, retaining the last accepted policies. Health check failures expose exception types only. Standard
application activity parentage and baggage remain intact, including when origin code uses its ambient authorization
context; MethodCache neither adds identity baggage nor strips the application's context.

## The tests

CI checkout collection and result reuse have a separate boundary from application caching. The collector only
removes coordinator-owned idle macOS clones with verified origin/full commit, terminal job results, and no
source changes or unexpected local data. It refuses redirected roots/results and keeps active checkouts and
unknown directories. Reuse requires the full SHA and all expected valid job exits; partial archives, failed
results and symlinked exit files cannot become a passing status. The regression suite under `build/ci/tests`
tests these protections. Build jobs still execute repository code as the build-server user; the collector is
not a sandbox for malicious pull requests.

Each row above is pinned by a test: `AdminEndpointSecurityTests` (ASP.NET Core), `HttpCacheIsolationTests`,
`HttpCacheEdgeCaseTests` and `UntrustedEntryTests` (HTTP caching), `CoreInstrumentationTests` (OpenTelemetry SDK) and `TelemetryPrivacyTests` (Core), `KeyInjectionPropertyTests` and
`DistributedCacheSecurityTests` (keys, tags and the distributed cache).

MessagePack key identity includes derived state in nested base-class/interface slots and collections. `PolymorphicKeyReviewTests` checks that distinct derived argument data cannot share a cache key.

HTTP age arithmetic saturates during response correction and resident-time accumulation, so an extreme upstream Age value cannot overflow the freshness calculation. `ResponseDelayReviewTests` covers these input boundaries.

Bulk publication guards are tested during key, tag and clear invalidation; in-flight batch results cannot resurrect invalidated entries. Collection-state properties and the generator signature corpus cover tenant/scope state on collection subclasses.

Persistent SQL insert locks, including other scopes sharing the table, cannot hold back newer invalidations or other committed gaps. The non-skipping `LongTransactionReviewTests` and `LockedGapReviewTests` in `MethodCache.Core.Tests/FaultInjection` exercise the production gap-discovery protocol without a database. Linked SQL integration variants additionally inject real locks through repeated polls and verify late commit/rollback recovery.

## Generated contract and registration boundaries

Generated code uses a versioned facade and captures policy, prefix and local publication generation together.
A key builder from another binding/snapshot is rejected before lookup. Retired loads cannot publish or serve
an old-generation fail-safe value. Descriptor/module state is separate from each container's live policy,
keyed binding and background scope. Unknown contracts fail before descriptor/factory execution; ambiguous
modules, candidates and unsupported cacheable bindings fail instead of choosing an implementation implicitly.
The corresponding tests are GeneratedContractTests, QuickStartRegistrationTests and the semantic signature corpus.

Policy-isolation tests reject application keys/tags that imitate internal method or namespace identities. Declarative
fingerprints use invariant length-framed UTF-16 and SHA-256; runtime namespaces use 16 random bytes per behavior change.
Typed method messages retire data without accepting policy from the sender. L2 reads enforce first-publication deadlines,
null/result predicates and original load-start tombstones, including after delayed transport completion. Format-5 malformed
header/truncation tests exercise the current parser. `PolicyBackendContract` runs the same assertions on Redis and SQL Server.

Generated methods always retain their method/service/type scope; the raw-key escape hatch is removed. Dynamic tag arguments use canonical scalar key formatting, keeping null, escaped strings and full-precision temporal values distinct. Reserved metadata keys and tags remain centrally framed and cannot be selected by caller-controlled strings.

Wire v1 rejects malformed lengths, mandatory flags/operations and unsupported versions before identity decoding.
A delivered incompatible frame clears local entries and warns once without its payload. Optional fields are bounded
and ignored. Entry deadlines are absolute and capped by the scope retention floor; logical key tombstones outlive
all entry formats and policies. Permanent HTTP epochs use atomic revisions with fresh initialization identities
after metadata loss or overflow. SQL initialization rejects unversioned/unknown schemas; use the delivered offline
migration for the known legacy layout. The roundtrip check is optional and cannot detect semantic losses in nested
derived or private state; explicit serializer configuration remains the application's responsibility.

The prefix-hash candidate uses process-secret Marvin32 (the .NET randomized string hash) for dictionary hashing; full key text is always compared
ordinally. Hash state is immutable per invocation snapshot and never becomes an L2/wire/admin identity. The
[algorithm and threat review](developer/prefix-hash-security.md) records the 32-bit dictionary output limitation,
reference sources and validation. `PrefixHashPropertyTests` pins the .NET reference algorithm, arbitrary UTF-16 splits and append
invalidation; `PrefixCollisionTests` forces collisions through all lookup/publication paths and checks eviction.
