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
- **Fast.** A hit with simple arguments allocates nothing on .NET 9 and later, is as fast as hand-written `IMemoryCache` code, and costs 4 to 6 times less than HybridCache or FusionCache.
- **Compile-time, not runtime.** A Roslyn source generator writes the decorators, analyzers catch mistakes in the editor, and Native AOT is tested in CI.
- **Tags that understand your arguments.** `"user:{id}"` becomes `user:42`, so an update invalidates exactly what it changed.
- **Tune it without redeploying.** Durations and tags come from attributes, a global default, `appsettings.json` (reloaded live) or runtime overrides.
- **Third-party clients too.** `[assembly: CacheInterface(typeof(IFxClient), nameof(IFxClient.GetRateAsync))]` caches chosen methods of an SDK client's own interface, no wrapper needed.
- **HTTP responses as well.** `AddHttpCaching()` (package `MethodCache.HttpCaching`) on an `HttpClient` honors `Cache-Control` and revalidates with ETags.
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

An explicit `SetAsync` with a lifetime shorter than L2 millisecond precision removes the previous distributed value instead of leaving it readable. If the backend is unavailable, the removal follows the normal invalidation retry path.

Overloaded generic methods include generic arity in their policy ids and key prefixes: `Get(int)`, `Get``1(int)` and `Get``2(int)` remain distinct. Generator and runtime configuration use the same ids; earlier keys for overloaded generic methods become cold misses.

Caller cancellation stops the wait during an L2 read or a slow factory, including bulk reads and fail-safe waits. L2 reads pass cancellation to the backend; shared factory work may finish for other callers. Caller cancellation is not reported as a dependency failure.

Policy tags are owned, read-only snapshots in both `CachePolicy` and `CacheRuntimePolicy`. Mutating the collection supplied by application code cannot change a published policy or bypass its version/change notifications.

A keyed `ICacheManager` registration remains independent of the default unkeyed manager. Registering another service key does not suppress `AddMethodCache()` or its default administration services.

Keyed registrations of cached interfaces are decorated under their own service key, which is part of their cache keys, so each registration caches its own results; an interface registered only keyed is not also registered without one. Generated decorators are disposable only when the interface is, so the container no longer keeps transient decorators resolved from the root.

Misconfiguration fails when the generic host starts rather than on the first call: GetOrCreate-only settings on a `[Cache]` method, and in `appsettings.json` invalid values, unknown setting names, a single value for `Tags` or a time span without `:`. `AddMethodCacheFromConfiguration` reads its section then, so configuration sources added after it count from the first call.

The attribute-move code fix preserves semantic binding across scopes and files: attribute/type aliases are qualified as needed, and constant arguments are materialized before they leave an implementation class.

On C# 13/.NET 9 or newer, generated decorators preserve `allows ref struct` constraints when the cached values and arguments themselves are ordinary cacheable types. Potentially ref-like type parameters used as cache arguments or results receive MCG007 instead of broken generated code. The generator still supports the .NET 8 compiler baseline.

Weighted capacity accounting remains nonnegative even for valid weights near `long.MaxValue`. Before an addition could overflow, the engine evicts existing weight; normal capacity/admission behavior is unchanged below that arithmetic limit.

SQL Server backplane tables require a non-nullable `binary(32)` `Scope` column. Incompatible schemas are reported
without automatic upgrades; see the MethodCache.SqlServer package guide.
