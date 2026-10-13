# Changelog

Changes that a user of the packages notices. The release steps are in
[docs/developer/RELEASE.md](docs/developer/RELEASE.md); when you tag a release, copy its section into the GitHub
release.

## Unreleased (1.0.0)

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
