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

Invalidation methods preserve synchronous `ref` and `ref readonly` returns. `Task` and `ValueTask` invalidation methods may use `ref`, `out` or `in` parameters: tags are captured after the underlying call returns, and invalidation runs only after its asynchronous operation succeeds.

Tag placeholders prefer the exact C# parameter spelling. A unique case-insensitive match is also accepted; ambiguous placeholders stay literal and report generator warning `MCG016`. This rule applies to both cached entries and invalidation.

Distributed tags and backplane keys/tags preserve exact UTF-16 code units, including lone surrogates. Envelope version 4 and backplane frames use lossless representations; older envelopes are cache misses. Deploy the same MethodCache version across nodes sharing a cache and backplane.

Redis escapes unpaired UTF-16 surrogates and backslashes in physical keys, keeping distinct logical keys separate in every data and invalidation operation. Ordinary keys and their prefixes stay unchanged.

An invalidated in-flight memory load cannot publish over a newer load or explicit write. Publication and invalidation are coordinated per load; application weighers run outside that coordination so they may call the cache safely.

`ExistsAsync` reports an entry only while it is both logically fresh and physically retained. A fail-safe retry window never makes existence extend beyond the physical fail-safe lifetime.

Fail-safe retry throttling is capped at physical expiration. Administration lists, entry details and removal results never report physically expired entries as live.

Tracing uses `ICacheManager.LookupAsync` to distinguish a cached null from a miss in one memory lookup, including disabled fast paths and refresh-ahead windows. Like `TryGetAsync`, it does not load from L2 or start a factory.
