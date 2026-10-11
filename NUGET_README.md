# MethodCache

**Cache any .NET method with one attribute.** MethodCache's source generator writes the caching code for you at
compile time: no reflection, no hand-written keys, no `TryGetValue` boilerplate, and it works with Native AOT.

```csharp
using MethodCache;

public interface IUserService
{
    [Cache(Duration = "00:10:00", Tags = ["user:{id}"])]
    Task<User> GetUserAsync(int id);

    [CacheInvalidate(Tags = ["user:{id}"])]
    Task RenameAsync(int id, string name);
}

builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddMethodCache();
```

- **One attribute, zero boilerplate.** Keys are built from the method and its arguments.
- **Fast.** A hit allocates nothing, is as fast as hand-written `IMemoryCache` code, and takes 4 to 6 times less time than HybridCache or FusionCache.
- **Tags that understand your arguments.** `"user:{id}"` becomes `user:42`.
- **Tune it without redeploying.** Attributes, global defaults, `appsettings.json` (reloaded live) and runtime overrides.
- **Third-party clients too.** `[assembly: CacheInterface(typeof(IFxClient), nameof(IFxClient.GetRateAsync))]` caches an SDK client's methods without a wrapper.
- **One line to go distributed.** `AddMethodCacheRedis(...)` or `AddMethodCacheSqlServer(...)`, with every instance's memory cache kept in sync.
- **Built for production.** Stampede protection, a W-TinyLFU memory cache, admin endpoints, a health check and OpenTelemetry.

Documentation, benchmarks and a runnable demo: https://github.com/eoniclabs/MethodCache
