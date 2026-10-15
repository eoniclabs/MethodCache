# Changelog

Changes that a user of the packages notices. The release steps are in
[docs/developer/RELEASE.md](docs/developer/RELEASE.md); when you tag a release, copy its section into the GitHub
release.

## Unreleased (1.0.0)

- **Breaking before 1.0:** all runtime packages now require .NET 10 and the .NET 10 SDK. Remove .NET 8/9 targets,
  their CI/package matrix and the allocating pre-.NET 9 lookup fallback. Frozen generated-contract fixtures remain unchanged.

### Core telemetry and attribute diagnostics

- Move instrumentation into Core's container-owned Meter/ActivitySource; the OpenTelemetry package now only subscribes.
  Replace provider/wrapper APIs with `MethodCacheTelemetryOptions`, one final request outcome and seconds histograms.
  Spans/logs omit exception payloads and copied request identities; key/tag recording is explicit opt-in.
- Replace object-memory estimates with local method/HTTP counts, HTTP bytes, observed distributed entries and
  `TotalWeight` in configured units. Health checks honor `failureStatus` and distinguish L2 availability from actual
  backplane connection state and the two recovery queues.


- Unify duration parsing and generator errors; add named attribute groups, enabled/distributed flags and fail-safe retry delay. Remove raw-key attributes and consolidate diagnostic IDs. Dynamic tag placeholders now use canonical key parts while existing contract-1 binaries retain their formatting.

- Omit invalid reload values and policy watcher exception payloads from diagnostic logs; retain error types.
- Drain HTTP generation recovery before disposing its transport. Retry once through an open breaker and report
  unconfirmed URL invalidations at shutdown without exposing targets.

### Core-owned HTTP storage

- Merge 304 response headers directly into the replacement representation, avoiding a temporary dictionary
  and per-response filter allocations when no Connection options are present.

- Avoid constructing duplicate response/content/header objects during foreground 304 revalidation; validate
  the returned representation before adding caller-only headers, and preserve background/error ownership.

- Release every retired local response body when its URL generation slot is invalidated, including colliding URLs.
  Avoid temporary identity/target buffers and reuse immutable local metadata during response replacement.

- Require explicit AddMethodCache alongside AddHttpCaching, in either order. Core owns a separate 100 MiB
  HTTP byte budget, or exclusively uses MethodCache's native Redis/SQL Server transport. Remove the public
  HTTP storage options/interface/entry extension points and the application-cache fallback.
- Capture shared atomic URL/client/static-tag/clear generations before origin work. Confirm unsafe mutation
  invalidation across nodes and value formats even with lost backplane messages; retain owned recovery when
  callers cancel. Unconfirmed mutations throw HttpCacheInvalidationException and must not be retried automatically.
- Add KeepCookies for a complete response-cache bypass, static HTTP tags, resident/observed administration and
  bounded body-free L2 observations. Core telemetry and health distinguish HTTP capability from method statistics.

### Owned distributed transports and stable wire contract

- Freeze one Core prefix and validated provider options at startup; use owned storage and serializer registrations. Application `IDistributedCache` is never a fallback. SQL connection strings can come from DI.
- Add wire v1 and entry format 6 with absolute deadlines, format-independent logical invalidation and shared retention bounds. Malformed mandatory frames clear local entries and warn once.
- Add native batched reads and permanent atomic epochs in Redis/SQL Server; version SQL schemas and ship an explicit legacy migration.
- Select JSON through copied options or a source-generated context. Add `VerifyRoundTrip` and `SerializerVersion`; internalize serializer/atomic-store interfaces.

### Cache API and policy isolation

- Reduce ICacheManager to four async operations with immutable options and explicit invalidation results.
- Retire local generations on effective policy changes, isolate runtime L2 namespaces and validate L2 lifetimes from first publication.
- Internalize policy/key/backplane implementation APIs and remove public cache callbacks and memory tuning switches.
- Keep the replaced fluent root/service/group coordinators internal; startup configuration uses `MethodCacheBuilder`
  and retains its public method configuration results.

### Generated contract and explicit startup registration

- Generated decorators use the versioned `MethodCache.Generated` facade. Each invocation captures one immutable
  policy/key snapshot; retired snapshots bypass caching and cannot publish into the current generation.
- Startup uses `MethodCacheBuilder`, with `Defaults`, `Group`, typed `Method` expressions and explicit module
  selection. Missing services require `AutoRegister(lifetime)` and exactly one accessible implementation.
  Cacheable late, open generic, catch-all keyed and unsupported bindings produce named errors.
- Built-in synchronous methods use a synchronous factory and single-flight path. Async-only custom managers
  bypass synchronous caching with a diagnostic. Distributed adapters without bounded native synchronous
  operations are unavailable for these calls; use async methods for L2 and distributed mutation invalidation.
- All six add-on packages require the exact matching Core package version. Frozen vendor binaries are tested
  under the current runtime and Native AOT without rebuilding their source during ordinary tests.

### Project review fixes

- Bulk factory results invalidated during their source read are not published; concurrent individual writes still win.
- Collection subclasses keep their public state in default, JSON and MessagePack keys. Collection subtypes needing runtime-state serialization now use hashed keys; earlier inline keys for those subtypes become cold misses.
- Static tags skip argument formatting, and dynamic tags format only referenced parameters.
- Redis backplane publication honors cancellation, and cancelled unsubscribe acknowledgements no longer fail application shutdown. SQL backplane gaps survive slow transactions; a separate non-waiting probe retires confirmed absent identities while newer invalidations and other committed gaps keep arriving.
- HTTP full replacement responses retire older entries when they cannot be stored. Stale-while-revalidate honors request freshness limits, and concurrent first variants share an index without hiding each other. Variant indexes outlive their bodies, and selection-rule changes retire the URL epoch once, preserving concurrent publications in the new epoch even when deleting the old entry fails after rotation.
- Backplane gap failure coverage runs without infrastructure in the Core fault-injection suite; real SQL lock checks supplement it.
- Memory benchmarks measure fresh fills and real tag invalidations; short-lived entries use the intended duration.
- SQL Server pins patched PKCS dependency versions for [CVE-2026-84008](https://github.com/advisories/GHSA-846f-f764-76mx), restoring the high-severity dependency audit gate.

### Remove retained compatibility code

The previous memory engine benchmark baseline is removed, and current runtime-key benchmarks have descriptive
names. SQL Server backplane initialization requires `Scope binary(32) NOT NULL`; automatic migration and cleanup
of unscoped rows are removed. Schema errors are logged, and table initialization uses the registered
`TimeProvider` for its startup deadline and retry interval. Benchmark `--list` commands no longer throw.

### Release audit fixes

The release audit fixes presence-aware manual reads, caller cancellation during loads, immutable policy tags,
HTTP response age and background request metadata, keyed registrations and OpenTelemetry ownership, generic
method constraints and attribute code fixes, and weighted capacity overflow. The web demo now stores atomic
price updates in SQLite, and the memory benchmark measures an uncached baseline.

MessagePack keys preserve derived member data in nested base-class/interface slots and collection elements.
Earlier keys for these arguments become cold misses. Overloaded generic methods include generic arity in their
policy ids and key prefixes, keeping different overloads distinct; earlier keys become cold misses.

### Third review fixes

- **HTTP caching** no longer stores a redirect the transport followed under the original URL (the transport drops
  `Authorization`, so one caller's response could reach another), invalidates the URL a `POST` was sent to also when
  it answers `303`, and removes its own validators so a retry of the same request gets a full response.
  `Vary: Accept-Encoding` works with automatic decompression, a `GET` with a body bypasses the cache, a
  `Cache-Control` or `Vary` that doesn't parse is not stored, clients that get caching from
  `ConfigureHttpClientDefaults` keep their own entries, far-future `Expires` no longer overflows, weak ETags stay weak,
  and the URL fragment is not part of the key. HTTP cache keys change, so entries from an earlier build are misses.
- **Loading and fail-safe**: a value invalidated or replaced while (or just before) its reload ran is no longer
  served as the fail-safe fallback to callers that joined the reload or a failed background refresh. A factory that
  requests the key it is loading now throws `InvalidOperationException` instead of hanging that key for every later
  caller. While a fail-safe value is reused, refresh-ahead no longer retries the method before `retryAfter`.
- **Refresh-ahead**: a `RefreshAhead` not shorter than `Duration` or `SlidingExpiration` is rejected by
  `CacheEntryOptions.Builder.Build()` and in `appsettings.json` (startup fails); one that comes from combining
  sources starts the reload halfway through the entry's life instead of reloading on every read.
- **Memory cache**: a manager that is never disposed no longer keeps its values (and the execution context of whoever
  created it) alive through its cleanup timer. `GetOrCreateManyAsync` no longer sends cached keys to the factory with
  `EnableFastPath = false` or inside a refresh window. The default `ICacheManager.TryGetFast` of a custom manager
  asks `LookupAsync`, so a cached `0`, `false` or `null` is a hit. `WithDuration`'s error message no longer promises
  entries that never expire.
- **Distributed cache**: a tag invalidation or clear another instance announces while a stamp read is on the wire is
  no longer forgotten (which promoted invalidated entries into memory). Invalidations queued during an outage are
  stored in batches, so a large backlog drains; more than 10,000, or one the cache keeps refusing, become one clear.
  On shutdown the queue is tried once more, after a retry already running (which stops after the batch it is storing), and what is lost is logged. Keys whose
  write timed out are removed at shutdown instead of after a delay nothing waits for, and further timed-out writes
  of a key push its one removal back instead of adding another. Shutdown also waits for writes still running (each capped
  by the operation timeout), so the removal one of them leaves behind is stored with the rest of the queue. A write that times out no longer opens the
  circuit breaker for the whole instance. A tag or clear stamp from a server whose clock runs ahead no longer turns
  every entry written since into a miss.
- **Serialization**: the default `JsonCacheSerializer` includes public fields, so value tuples and structs with
  fields are shared through the distributed cache instead of coming back empty. Each value written is read back
  first; values that don't survive that (private setters, a derived type
  returned as its base type, `object`) stay in memory only, with one warning, instead of handing other instances a
  value with missing data. Collections declared as interfaces are still shared when they come back as another
  implementation. A derived value nested in a collection or property isn't detected. Including fields changes two
  things: a type with a public field the serializer can't write (a delegate, a reference back to the object) now
  fails to serialize, and a public readonly field of a type with a parameterless constructor doesn't read back.
  Both now stay in memory only with one warning; before, the field was left out and the rest was shared. A value the
  serializer throws on now warns once per method and type instead of on every write. Entries an earlier build wrote
  for types with public fields are read without those fields until they expire.
- **Redis**: a connection factory that threw once (Redis down at first use) is asked again on the next call instead
  of failing until restart, and the backplane retries its subscription with backoff (1 s doubling to 30 s, a warning
  per failure) instead of never subscribing. Once it subscribes after failing, it clears this instance's memory,
  as after a reconnect, since invalidations sent meanwhile never arrived. The docs now say to run Redis with `maxmemory-policy noeviction`: an evicted invalidation
  stamp brings invalidated values back.
- **SQL Server**: a `TagRetention` that ends after 9999-12-31 fails at startup instead of making every tag
  invalidation fail, and stamp expiry is capped there. Expired rows are deleted every 5 minutes instead of 30, so each
  delete stays small.
- **Generated decorators** compile for generic methods constrained to a base class (`where T : Entity`), keep
  `scoped` on forwarded parameters, escape U+2028/U+2029 in tag and group literals, skip `file` classes when picking
  an implementation to register, and no longer clash (CS0436) with a referenced project's generated code through
  `InternalsVisibleTo`. `[Cache]` or `[CacheInvalidate]` on a `static`, non-public or `sealed` interface member is now
  warning MCG017 instead of being ignored. The IDE builds each decorator once per edit, not once per cached method.
- **Keys**: two cached methods with the same name on different interfaces of one hierarchy (`ICatalog : ILookup`, or
  `IC : IA, IB`) no longer share entries; their keys add the parameter types, and the declaring interface when the
  signatures are equal. `-0.0` and `0.0` are two keys in hashed keys too, `NaN` and infinities inside a hashed argument
  no longer throw, and `[CacheKey(UseAsRawKey = true)]` dates are written like generated keys: they keep fractions of a second and
  their offset, and a local time is written as UTC, so the key doesn't depend on the server's time zone. Keys of
  these methods and arguments change, so their entries from an earlier build are misses.
- **Metrics and spans** name a cached method by its id (`MyApp.IUserService.GetAsync`) instead of its bare name, so
  same-named methods on different interfaces are separate series, as the OpenTelemetry README shows. A method
  inherited from a generic interface reports the closed type it comes from, so `IUserRepo : IRepository<User>` and
  `IOrderRepo : IRepository<Order>` report `IRepository<User>.Get` and `IRepository<Order>.Get`.
- **Framework types in keys**: `BigInteger`, `Complex`, `IPAddress`, `JsonElement`, `Half`, `Int128`, `DateOnly`,
  `Uri`, `Version` and other immutable .NET types are written by their value, in every key generator and inside
  records and collections. Before, the decorator wrote `BigInteger`'s `IsZero` and `Sign`, so 3 and 5 shared one
  entry, and `IPAddress` threw. Other types from .NET, ASP.NET Core or Microsoft.Extensions in a key, such as
  `StringBuilder` or `CultureInfo`, are now build error MCG018 instead of a key that ignores their value. Keys of arguments with these
  types change, so their entries from an earlier build are misses.
- **Result types a distributed cache can't restore** (`object`, an interface or an abstract class, also as an
  element) get warning MCG019: with a distributed cache, the default round-trip check keeps those results in memory
  only.
- **`[assembly: CacheInterface]` in a class library** decorates the client even when none of the library's code has
  run before `AddMethodCache()`, also when several threads resolve services while the library's initializer runs.
- **Dependency injection.** Generated decorators implement `IDisposable`/`IAsyncDisposable` only when the interface
  does, so the container no longer keeps every transient decorator resolved from the root (their `ownsDecorated`
  constructor parameter is gone). Keyed registrations are decorated under their own key, with the service key in
  their cache keys (`MyApp.IRates.Get@ecb:1`, see `MethodCacheKeys.KeyedMethodName`), so two registrations of one
  interface never share entries; a key that isn't a string, enum, bool, char, integer or `Guid` makes
  `AddMethodCache()` throw. An interface registered only keyed no longer gets an extra unkeyed singleton. When two assemblies generate a decorator for the same
  interface (`[assembly: CacheInterface]` in a library and in the app), the one in the assemblies passed to
  `AddMethodCache` (by default the caller's) or else the entry assembly's is used, instead of whichever module
  initializer ran last; otherwise `AddMethodCache` throws naming both.
- **Startup validation.** The generic host loads every policy source when it starts, so startup-code
  misconfiguration and invalid appsettings fail `StartAsync` and unknown method ids are logged then, not on the first
  call. `AddMethodCacheFromConfiguration` reads its section at that point, so configuration sources added after the
  call count. appsettings now rejects unknown setting names, a single value for `Tags`, an object for a method's
  `FailSafe` and time spans without `:` (`"300"` was 300 days). The resolved policy of a method keeps the newest
  version when two sources change it concurrently. The `MethodId` property for environment variables is documented.
- **OpenTelemetry and admin.** `ICacheMetricsProvider.CacheLatency` takes a `TimeSpan`, and `load_duration` keeps
  sub-millisecond precision. `EnableMetrics = false` keeps the app's own metrics provider, and options from several
  calls or `Configure<OpenTelemetryOptions>` compose instead of resetting. A hit no longer writes a shared counter
  when nothing listens; the hit ratio comes from the cache's statistics. `SetCacheUserId(null)` and
  `SetCacheTenantId(null)` clear the value. Admin `DELETE entry` answers 204 with a distributed cache, since the
  removal reached it and the other instances. The web demo's `.http` walkthrough loads product 2 before showing it
  is still cached.
- **Tags combine across policy layers.** Tags from startup code, `appsettings.json`, runtime overrides and the
  defaults are added to the `[Cache]` attribute's tags instead of replacing them, so a `[CacheInvalidate]` on an
  attribute tag always reaches the entry. To replace them, call the new `CacheEntryOptions.Builder.ClearTags()`
  (`CachePolicyFields.ClearTags` for a raw policy) or set `"ClearTags": true` for a method in `appsettings.json`.
  The docs no longer say runtime overrides win over everything: a method's own override wins over its other layers,
  while runtime defaults (`DefaultPolicy`, `ForGroup` in `ApplyAsync`) only fill in what the method leaves unset, as
  before.
- **Background loads of scoped services.** A refresh-ahead, and a fail-safe call that goes on after its caller got
  the expired value, now run a scoped or transient service on a new instance from a new DI scope (keyed services
  keep their key), disposed when the load ends, instead of on the request's instance after its scope was disposed.
  Every scoped or transient cached service the load calls gets its own scope, and a call left running after the load
  ended gets none. The scope factory comes from the root container. Singletons are unchanged. Generated decorators call the new `DecoratorServices.ForLoad`, so rebuild the projects
  that generate them together with the new MethodCache.Core.

### If you used a pre-release build from GitHub Packages

- **Generated `Add{Interface}WithCaching` methods are gone.** Combined with `AddMethodCache()` they registered the
  service twice. Register the interface the usual way (with a factory if you need one), before `AddMethodCache()`.
- **Keys for array and collection arguments.** The decorator now writes them into the key itself
  (`ByIdsAsync:[3:1:2:3]`) instead of hashing them, so they also work under Native AOT. Distributed cache entries
  written by a pre-release build for such methods are misses and expire normally.

- **HTTP cache keys.** Keys are now length-framed, so every HTTP response cache key changes, including for requests
  without `Vary` headers. Entries written by a pre-release build are not read again; this applies to the built-in and
  to custom HTTP cache storage. Let them expire, or clear the storage namespace when you deploy.
- **Distributed invalidation.** Backplane key and tag messages use new message types and lossless UTF-16 encoding,
  and distributed cache entries use envelope version 4. Invalidations do not cross between a pre-release build and
  1.0.0 in either direction: a node ignores backplane messages from another MethodCache version and logs one warning,
  and entries written by a pre-release build are cache misses. Upgrade every node that shares a cache and backplane
  together, with a cache clear, or use a new cache namespace.
- **SQL Server tables.** The invalidations table gets a nullable `Scope binary(32)` column and the indexes
  `Index_Scope_Id (Scope, Id)` and `Index_Scope_CreatedAt (Scope, CreatedAt)`; with `CreateTablesIfMissing = false`,
  add them in your migration before deploying. Rows without a scope are ignored and deleted once they are older than
  `BackplaneRetention`. Cache rows written by a pre-release build (unhashed keys) become misses and expire normally.
  Upgrade every instance that shares the tables together, and restart them with empty in-memory caches.

Diagnostic migration: MC0001 stays MC0001; old duration MC0004 → MC0002; MCG001–MCG007 → MC0003–MC0009; MCG009–MCG019 → MC0010–MC0020. MC0021 rejects explicit negative versions. Update analyzer suppressions and `.editorconfig`; migrate `[Cache("group")]` to `[Cache(Group = "group")]` and remove `[CacheKey]`. Canonical `ICacheKeyProvider` parts and manual `ICacheManager` keys replace raw-key use cases.
