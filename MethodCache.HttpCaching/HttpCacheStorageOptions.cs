namespace MethodCache.HttpCaching;

/// <summary>
/// Settings for the built-in in-memory HTTP cache, shared by every HttpClient that uses HTTP caching.
/// Set them with <c>services.Configure&lt;HttpCacheStorageOptions&gt;(...)</c>.
/// </summary>
public sealed class HttpCacheStorageOptions
{
    /// <summary>Total size of cached response bodies. Default 100 MB; 0 means no limit.</summary>
    public long MaxCacheSize { get; set; } = 100 * 1024 * 1024;
}
