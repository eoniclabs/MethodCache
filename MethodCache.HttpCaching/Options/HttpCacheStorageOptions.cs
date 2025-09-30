namespace MethodCache.HttpCaching.Options;

public class HttpCacheStorageOptions
{
    public long MaxCacheSize { get; set; } = 100 * 1024 * 1024; // 100 MB
    public long MaxResponseSize { get; set; } = 10 * 1024 * 1024; // 10 MB
}
