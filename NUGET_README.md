# MethodCache

**Cache any .NET method with one attribute.** MethodCache's source generator writes the caching code for you at
compile time: no reflection, no hand-written keys, no `TryGetValue` boilerplate, and it works with Native AOT.

<!-- snippet: readme-quickstart-interface -->
```cs
using MethodCache;

public interface IUserService
{
    [Cache(Duration = "00:10:00", Tags = ["user:{id}"])]
    Task<User> GetUserAsync(int id);

    [CacheInvalidate(Tags = ["user:{id}"])]
    Task RenameAsync(int id, string name);
}
```
<!-- endSnippet -->

<!-- snippet: readme-quickstart-registration -->
```cs
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddMethodCache();
```
<!-- endSnippet -->

- **One attribute, zero boilerplate.** Keys are built from the method and its arguments, including records and collections (`GetAsync:42`, `ByIdsAsync:[3:1:2:3]`; values of different types never share a key, nested ones included); your service stays plain code.
  Register services before `AddMethodCache`; missing registrations need explicit `AutoRegister(lifetime)` and an
  unambiguous generated module. Startup configuration uses `MethodCacheBuilder`.
- **Fast.** A hit with simple arguments allocates nothing on .NET 10, is as fast as hand-written `IMemoryCache` code, and costs 4 to 6 times less than HybridCache or FusionCache.
- **Compile-time, not runtime.** A Roslyn source generator writes the decorators, analyzers catch mistakes in the editor, and Native AOT is tested in CI.
  Previously compiled contract-1 vendor decorators keep working without regeneration, with both inline and materialized keys.
- **Tags that understand your arguments.** `"user:{id}"` becomes `user:42`, so an update invalidates exactly what it changed.
- **Tune it without redeploying.** Durations and tags come from attributes, a global default, `appsettings.json` (reloaded live) or runtime overrides.
- **Third-party clients too.** `[assembly: CacheInterface(typeof(IFxClient), nameof(IFxClient.GetRateAsync))]` caches chosen methods of an SDK client's own interface, no wrapper needed.
- **HTTP responses as well.** `AddHttpCaching()` (package `MethodCache.HttpCaching`) on an `HttpClient` honors `Cache-Control` and revalidates with ETags. Register `AddMethodCache` too: Core owns a separate 100 MiB HTTP budget, or uses its configured native Redis/SQL transport exclusively. `KeepCookies` preserves cookies and bypasses response caching; confirmed mutations retire shared generations. Graceful async disposal retries pending invalidations once and reports any lost count; recovery is not durable across process exit. Local client-tag invalidation reclaims matching bodies through a scan of resident HTTP entries.
- **One line to go distributed.** `AddMethodCacheRedis(...)` or `AddMethodCacheSqlServer(...)` (packages `MethodCache.Redis` and `MethodCache.SqlServer`) shares results between instances and keeps every instance's memory cache in sync.
- **Built for production.** Stampede protection, fail-safe, a W-TinyLFU memory cache that stays within its size limit under load, admin endpoints, a health check and OpenTelemetry tracing and metrics.

Documentation, benchmarks and a runnable demo: https://github.com/eoniclabs/MethodCache

## Additional behavior

With a distributed cache, values are JSON by default, public fields included, so value tuples and structs with
fields are shared. Each value is read back before it is shared; a method whose values don't come back unchanged
(private setters, a derived type returned as its base type, `object`) is cached in memory only for that type, with
one warning. Entries an earlier build wrote for types with public fields are read without those fields until they
expire.

MessagePack keys preserve derived member data in nested base-class/interface slots and collection elements. Earlier keys for these arguments change; existing entries become cold misses and expire normally.

Manual `TryGetAsync` reads a fresh entry, including a cached null, even when the fast path is disabled or refresh-ahead is due. This lookup does not start a factory or background refresh.

Overloaded generic methods include generic arity in their policy ids and key prefixes: `Get(int)`, `Get``1(int)` and `Get``2(int)` remain distinct. Generator and runtime configuration use the same ids; earlier keys for overloaded generic methods become cold misses.

Caller cancellation stops the wait during an L2 read or a slow factory, including bulk reads and fail-safe waits. L2 reads pass cancellation to the backend; shared factory work may finish for other callers. Caller cancellation is not reported as a dependency failure.

Bulk reads guard their source work against key, tag and clear invalidation: an invalidated batch result is returned to its caller but is not published. Collection subclasses keep their own public properties and fields in hashed keys, as well as their elements. Static tags do not format arguments; templates format only the parameters they reference. Redis backplane publications honor caller cancellation, and a cancelled unsubscribe acknowledgement does not fail application shutdown. SQL backplane gaps never hold newer invalidations behind a locked transaction, and HTTP variant schema changes retire their publication epoch once; a failed deletion after a successful rotation does not retire a newer concurrent publication.

Published policy tags are owned, read-only snapshots. Mutating the collection supplied by application code cannot change a published policy or bypass its version/change notifications.

A keyed `ICacheManager` registration remains independent of the default unkeyed manager. Registering another service key does not suppress `AddMethodCache()` or its default administration services.

Keyed registrations of cached interfaces are decorated under their own service key, which is part of their cache keys, so each registration caches its own results; an interface registered only keyed is not also registered without one. A catch-all `KeyedService.AnyKey` registration is rejected with a named binding error; register concrete keys instead. Generated decorators are disposable only when the interface is, so the container no longer keeps transient decorators resolved from the root.

Misconfiguration fails when the generic host starts rather than on the first call: invalid values, unknown setting names, a single value for `Tags` or an invalid duration in `appsettings.json`. Positive invariant unit durations such as `30s` are accepted. `AddMethodCache(cache => cache.FromConfiguration(configuration))` reads its section at startup, so configuration sources added later apply from the first call.

The attribute-move code fix preserves semantic binding across scopes and files: attribute/type aliases are qualified as needed, and constant arguments are materialized before they leave an implementation class.

With C# 13 or newer, generated decorators preserve `allows ref struct` constraints when the cached values and arguments themselves are ordinary cacheable types. Potentially ref-like type parameters used as cache arguments or results receive MC0009 instead of broken generated code. The packages require .NET 10 and the .NET 10 SDK. C# 12 source remains supported when explicitly selected in that SDK.

Long namespace and type names work with `EmitCompilerGeneratedFiles`; generated files use short, stable names when needed.

Weighted capacity accounting remains nonnegative even for valid weights near `long.MaxValue`. Before an addition could overflow, the engine evicts existing weight; normal capacity/admission behavior is unchanged below that arithmetic limit.

SQL Server backplane tables require a non-nullable `binary(32)` `Scope` column. Incompatible schemas are reported
without automatic upgrades; see the MethodCache.SqlServer package guide.

The cache manager has four operations with cancellation last and immutable options. `WithEnabled(false)` bypasses
caching; `WithDistributed(false)` uses only memory. Policy changes retire local generations. Declarative settings
share a stable L2 namespace, while runtime overrides use private namespaces. Invalidation results distinguish local
mutation, confirmed L2 metadata, queued recovery and confirmed/queued backplane sends. See
[configuration](docs/features/configuration.md) and [distributed guarantees](docs/features/distributed-cache.md).

Attribute settings use named arguments, for example `Group`, `Enabled`, `Distributed` and `RetryAfter`. Durations accept invariant positive values such as `30s`, `1.5h` or `00:05:00`. Generated keys always preserve method scope; `CacheKeyAttribute` has been removed. See [configuration](docs/features/configuration.md) and [keys](docs/features/keys.md).

Distributed providers use one container-bound `DistributedCacheOptions.KeyPrefix` for data and invalidation. Value formats and serializer schemas have separate namespaces; metadata retention and atomic epochs prevent older physical variants from reappearing after invalidation. SQL Server uses an explicit versioned schema and delivered migration scripts. See [distributed caching](docs/features/distributed-cache.md).

Core emits `MethodCache` metrics and activities through the container's `IMeterFactory`; the OpenTelemetry add-on
only subscribes with `AddMethodCacheInstrumentation()`. Keys/tags are off by default, histograms use seconds, and pure
hits have no spans. Administrator statistics distinguish local method/HTTP entries and HTTP bytes; `TotalWeight`
uses the configured weigher's units (one per method entry by default), with no object-memory estimate.

Dictionary hashes are process-local implementation metadata. Canonical key text, L2 identities, administration and
invalidation remain stable across processes and restarts; the lookup hash is never stored or transmitted.

Memory-only asynchronous misses avoid allocating closures for inactive fail-safe and distributed-write branches.

Logical-key lookups and invalidations use the same framing and optional version as storage, including keys starting
with NUL. Admin mutations return 503 while distributed invalidation awaits confirmation; recovery remains owned by
the cache. Incompatible distributed scope contracts use memory only and report an actionable unhealthy status.
See [manual keys](docs/features/manual-caching.md#versions-and-the-stored-key) and [monitoring](docs/features/monitoring.md).

Generated method keys and policy ids include the service assembly simple name (without its version), so equally named interfaces in different assemblies stay isolated. Build keys with `MethodCacheKeys`; read policy ids from the admin method list.
