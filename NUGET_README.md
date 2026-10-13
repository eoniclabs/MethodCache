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

- **One attribute, zero boilerplate.** Keys are built from the method and its arguments (values of different types never share a key, nested ones included); your service stays plain code.
- **Fast.** A hit with simple arguments allocates nothing on .NET 9 and later, is as fast as hand-written `IMemoryCache` code, and costs 4 to 6 times less than HybridCache or FusionCache.
- **Compile-time, not runtime.** A Roslyn source generator writes the decorators, analyzers catch mistakes in the editor, and Native AOT is tested in CI.
- **Tags that understand your arguments.** `"user:{id}"` becomes `user:42`, so an update invalidates exactly what it changed.
- **Tune it without redeploying.** Durations and tags come from attributes, a global default, `appsettings.json` (reloaded live) or runtime overrides.
- **Third-party clients too.** `[assembly: CacheInterface(typeof(IFxClient), nameof(IFxClient.GetRateAsync))]` caches chosen methods of an SDK client's own interface, no wrapper needed.
- **HTTP responses as well.** `AddHttpCaching()` (package `MethodCache.HttpCaching`) on an `HttpClient` honors `Cache-Control` and revalidates with ETags.
- **One line to go distributed.** `AddMethodCacheRedis(...)` or `AddMethodCacheSqlServer(...)` (packages `MethodCache.Redis` and `MethodCache.SqlServer`) shares results between instances and keeps every instance's memory cache in sync.
- **Built for production.** Stampede protection, fail-safe, a W-TinyLFU memory cache that stays within its size limit under load, admin endpoints, a health check and OpenTelemetry tracing and metrics.

Documentation, benchmarks and a runnable demo: https://github.com/eoniclabs/MethodCache
