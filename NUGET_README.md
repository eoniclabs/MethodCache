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

- **One attribute, zero boilerplate.** Keys are built from the method and its arguments, and values of different types never share a key, nested ones included. Generic interfaces such as `IRepository<User>` work too. Overloaded methods include namespace-qualified parameter types in their identities. Arguments with only private state, including inherited fields, are refused. Custom key providers can safely compose other keys. Generic interfaces and methods preserve their `unmanaged` constraints.
- **Fast.** A hit allocates nothing, is as fast as hand-written `IMemoryCache` code, and takes 4 to 6 times less time than HybridCache or FusionCache.
- **Tags that understand your arguments.** `"user:{id}"` becomes `user:42`.
- **Tune it without redeploying.** Attributes, global defaults, `appsettings.json` (reloaded live) and runtime overrides.
- **Third-party clients too.** `[assembly: CacheInterface(typeof(IFxClient), nameof(IFxClient.GetRateAsync))]` caches an SDK client's methods without a wrapper.
- **One line to go distributed.** `AddMethodCacheRedis(...)` or `AddMethodCacheSqlServer(...)`, with every instance's memory cache kept in sync.
- **Built for production.** Stampede protection, a W-TinyLFU memory cache that stays within its size limit under load, admin endpoints, a health check and OpenTelemetry.

[Supported signatures and forwarded members](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/source-generator.md) include properties, events and indexers whose names are C# keywords.

Concurrent runtime overrides preserve mutation order: delayed notifications cannot replace a newer policy or
remove a renewed override. See [configuration](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/configuration.md).

Bulk reads return the value selected by the cache, including a distributed hit or a concurrent writer, so they
agree with individual reads.

Memory statistics estimate cached payloads, including current string and array lengths. A configured weigher
reports its total weight instead of that estimate. Sampling bounds serialization with `SamplingMaxEntries` and
reuses estimates according to `SamplingRecalculationInterval`.

Documentation, benchmarks and a runnable demo: https://github.com/eoniclabs/MethodCache
