# Changelog

Changes that a user of the packages notices. The release steps are in
[docs/developer/RELEASE.md](docs/developer/RELEASE.md); when you tag a release, copy its section into the GitHub
release.

## Unreleased (1.0.0)

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
  no longer throw, and `[CacheKey(UseAsRawKey = true)]` dates keep fractions of a second and their `Kind`. Keys of
  these methods and arguments change, so their entries from an earlier build are misses.
- **Metrics and spans** name a cached method by its id (`MyApp.IUserService.GetAsync`) instead of its bare name, so
  same-named methods on different interfaces are separate series, as the OpenTelemetry README shows.
- **Framework types in keys**: `BigInteger`, `Complex`, `IPAddress`, `JsonElement`, `Half`, `Int128`, `DateOnly`,
  `Uri`, `Version` and other immutable .NET types are written by their value, in every key generator and inside
  records and collections. Before, the decorator wrote `BigInteger`'s `IsZero` and `Sign`, so 3 and 5 shared one
  entry, and `IPAddress` threw. Other `System` or `Microsoft` types in a key, such as `StringBuilder` or
  `CultureInfo`, are now build error MCG018 instead of a key that ignores their value. Keys of arguments with these
  types change, so their entries from an earlier build are misses.
- **Result types a distributed cache can't restore** (`object`, an interface or an abstract class, also as an
  element) get warning MCG019: with a distributed cache those results are kept in memory only.
- **`[assembly: CacheInterface]` in a class library** decorates the client even when none of the library's code has
  run before `AddMethodCache()`.

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
