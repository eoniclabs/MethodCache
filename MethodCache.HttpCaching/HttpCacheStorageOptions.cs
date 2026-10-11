namespace MethodCache.HttpCaching;

/// <summary>
/// Settings for the built-in HTTP cache storage, shared by every HttpClient that uses HTTP caching.
/// Set them with <c>services.Configure&lt;HttpCacheStorageOptions&gt;(...)</c>.
/// </summary>
public sealed class HttpCacheStorageOptions
{
    /// <summary>Total size of cached response bodies in memory. Default 100 MB; 0 means no limit.</summary>
    public long MaxCacheSize { get; set; } = 100 * 1024 * 1024;

    /// <summary>
    /// Keep responses in the registered <c>IDistributedCache</c> instead of memory, for example the Redis or SQL Server
    /// cache that <c>AddMethodCacheRedis()</c> / <c>AddMethodCacheSqlServer()</c> register, so every instance of the app
    /// shares them, an ETag fetched by one instance is revalidated by all, and a POST on one instance invalidates for
    /// all. Default <c>false</c>.
    /// </summary>
    public bool UseDistributedCache { get; set; }

    /// <summary>
    /// Prefix for keys in the distributed cache. Give each application its own when several share one Redis or
    /// SQL Server. Default <c>"methodcache-http:"</c>.
    /// </summary>
    public string KeyPrefix { get; set; } = "methodcache-http:";
}
