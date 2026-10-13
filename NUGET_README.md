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

Non-public default interface helpers are supported. Interfaces with non-public abstract members report `MCG013`.

- **One attribute, zero boilerplate.** Keys are built from the method and its arguments, and values of different types never share a key, nested ones included, with exact UTF-16 string identity across key generators. Generic interfaces such as `IRepository<User>` and interfaces nested in generic types work too, including escaped keyword and reserved-looking parameter names.
- **Fast.** A hit allocates nothing, is as fast as hand-written `IMemoryCache` code, and takes 4 to 6 times less time than HybridCache or FusionCache.
- **Tags that understand your arguments.** `"user:{id}"` becomes `user:42`.
- **Tune it without redeploying.** Attributes, global defaults, `appsettings.json` (reloaded live) and runtime overrides.
- **Third-party clients too.** Decoration preserves normal DI disposal ownership. `[assembly: CacheInterface(typeof(IFxClient), nameof(IFxClient.GetRateAsync))]` caches an SDK client's methods without a wrapper.
- **One line to go distributed.** L2 lifetimes include time spent loading, so entries cannot outlive their tag/clear metadata. `AddMethodCacheRedis(...)` or `AddMethodCacheSqlServer(...)`, with every instance's memory cache kept in sync. Your own distributed cache must implement `IAtomicInvalidationStore` as well as `IDistributedCache`, or the host fails at startup.
- **Built for production.** Stampede protection, a W-TinyLFU memory cache that stays within its size limit under load, admin endpoints, a health check and OpenTelemetry.

`CacheEntryOptions.Builder.Build()` snapshots tags and callbacks; reusing the builder does not change previously built options. Fluent key versions cannot alias unversioned user keys.

Documentation, benchmarks and a runnable demo: https://github.com/eoniclabs/MethodCache

## Behavior and compatibility

Invalidation methods preserve synchronous `ref` and `ref readonly` returns. `Task` and `ValueTask` invalidation methods may use `ref`, `out` or `in` parameters: tags are captured after the underlying call returns, and invalidation runs only after its asynchronous operation succeeds.

Tag placeholders prefer the exact C# parameter spelling. A unique case-insensitive match is also accepted; ambiguous placeholders stay literal and report generator warning `MCG016`. This rule applies to both cached entries and invalidation.

Distributed tags and backplane keys/tags preserve exact UTF-16 code units, including lone surrogates. Envelope version 4 and backplane frames use lossless representations; older envelopes are cache misses. Deploy the same MethodCache version across nodes sharing a cache and backplane.

Redis escapes unpaired UTF-16 surrogates and backslashes in physical keys, keeping distinct logical keys separate in every data and invalidation operation. Ordinary keys and their prefixes stay unchanged.

An invalidated in-flight memory load cannot publish over a newer load or explicit write. Publication and invalidation are coordinated per load; application weighers run outside that coordination so they may call the cache safely.

`ExistsAsync` reports an entry only while it is both logically fresh and physically retained. A fail-safe retry window never makes existence extend beyond the physical fail-safe lifetime.

Fail-safe retry throttling is capped at physical expiration. Administration lists, entry details and removal results never report physically expired entries as live.

Tracing uses `ICacheManager.LookupAsync` to distinguish a cached null from a miss in one memory lookup, including disabled fast paths and refresh-ahead windows. Like `TryGetAsync`, it does not load from L2 or start a factory.

Factory registration helpers dispose owned implementations even when their service interface does not declare disposal. Asynchronous disposal prefers `IAsyncDisposable`, then falls back to `IDisposable`. Automatically discovered DI services retain container ownership.

Sealed default interface helpers retain their interface bodies and are never reimplemented by a decorator.

Backplane invalidations do not cross wire versions in either direction. Deploy the same MethodCache version on all nodes sharing a cache/backplane; a rolling upgrade needs a new cache namespace or a coordinated cache clear. New message type numbers prevent legacy keys from being mistaken for encoded frames. Rejected frames emit one warning per backplane instance without logging their contents.

`TagRetention` must exceed `ClockSkewTolerance`; invalid settings fail startup or direct layer construction. L2 entries live at most `TagRetention - ClockSkewTolerance` from their source read, capped further by their policy duration.

Generated decorators qualify framework and cache infrastructure type names globally, so application types such as `Task`, `List` or `CachedMethod` cannot shadow them.

Runtime tag templates match against every method parameter, even when the attributes use only a subset. Exact names take precedence; an ambiguous case-insensitive placeholder remains literal. Untagged policies do not format argument values.

Cached interfaces may declare nested helper interfaces and abstract types. These declarations do not trigger MCG013.

An invalidation attribute without tags forwards the operation without issuing an empty tag invalidation, including asynchronous methods with reference parameters.

Versioned fluent entries occupy a reserved namespace. Use `CacheManagerExtensions.GetCacheKey(logicalKey, version)` when directly invalidating or administering one; do not construct its internal control-character frame by hand. `CacheContext.Key` keeps the logical key for callbacks and factories.

L2 timestamps have millisecond precision: `TagRetention` and `ClockSkewTolerance` are each truncated to whole milliseconds before validation and lifetime calculation. Their effective difference must be at least one millisecond; settings that collapse to zero fail startup or direct construction.

Unversioned fluent keys that begin with NUL are escaped too. Use `CacheManagerExtensions.GetCacheKey(logicalKey)` for direct invalidation or administration of those entries, just as you use the overload with a version for versioned entries.

Generated runtime type checks use the same generic-parameter aliases as decorator signatures, including unsealed generic argument types whose simple members form inline cache keys.

MessagePack keys preserve derived member data in nested base-class/interface slots and collection elements. Earlier keys for these arguments change; existing entries become cold misses and expire normally.

Manual `TryGetAsync` reads a fresh entry, including a cached null, even when the fast path is disabled or refresh-ahead is due. This lookup does not start a factory or background refresh.

An explicit `SetAsync` with a lifetime shorter than L2 millisecond precision removes the previous distributed value instead of leaving it readable. If the backend is unavailable, the removal follows the normal invalidation retry path.

Overloaded generic methods include generic arity in their policy ids and key prefixes: `Get(int)`, `Get``1(int)` and `Get``2(int)` remain distinct. Generator and runtime configuration use the same ids; earlier keys for overloaded generic methods become cold misses.

Caller cancellation stops the wait during an L2 read or a slow factory, including bulk reads and fail-safe waits. Shared work may finish for other callers; cancellation is not reported as a dependency failure.

Policy tags are owned, read-only snapshots in both `CachePolicy` and `CacheRuntimePolicy`. Mutating the collection supplied by application code cannot change a published policy or bypass its version/change notifications.

A keyed `ICacheManager` registration remains independent of the default unkeyed manager. Registering another service key does not suppress `AddMethodCache()` or its default administration services.

The attribute-move code fix preserves semantic binding across scopes and files: attribute/type aliases are qualified as needed, and constant arguments are materialized before they leave an implementation class.
