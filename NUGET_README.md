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

- **One attribute, zero boilerplate.** Keys are built from the method and its arguments, and values of different types never share a key, nested ones included. Generic interfaces such as `IRepository<User>` work too.
- **Fast.** A hit allocates nothing, is as fast as hand-written `IMemoryCache` code, and takes 4 to 6 times less time than HybridCache or FusionCache.
- **Tags that understand your arguments.** `"user:{id}"` becomes `user:42`.
- **Tune it without redeploying.** Attributes, global defaults, `appsettings.json` (reloaded live) and runtime overrides.
- **Third-party clients too.** `[assembly: CacheInterface(typeof(IFxClient), nameof(IFxClient.GetRateAsync))]` caches an SDK client's methods without a wrapper.
- **One line to go distributed.** `AddMethodCacheRedis(...)` or `AddMethodCacheSqlServer(...)`, with every instance's memory cache kept in sync. Your own distributed cache must implement `IAtomicInvalidationStore` as well as `IDistributedCache`, or the host fails at startup.
- **Built for production.** Stampede protection, a W-TinyLFU memory cache that stays within its size limit under load, admin endpoints, a health check and OpenTelemetry.

`CacheEntryOptions.Builder.Build()` snapshots tags and callbacks; reusing the builder does not change previously built options. Fluent key versions cannot alias unversioned user keys.

Documentation, benchmarks and a runnable demo: https://github.com/eoniclabs/MethodCache
