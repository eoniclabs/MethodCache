namespace MethodCache.HttpCaching.Options;

public class CacheBehaviorOptions
{
    public bool RespectCacheControl { get; set; } = true;
    public bool RespectVary { get; set; } = true;
    public bool IsSharedCache { get; set; } = false;
    public bool EnableStaleWhileRevalidate { get; set; } = false;
    public bool EnableStaleIfError { get; set; } = false;
    public TimeSpan BackgroundRevalidationTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan MaxStaleIfError { get; set; } = TimeSpan.FromHours(24);
    public bool RespectMustRevalidate { get; set; } = true;
    public bool RespectProxyRevalidate { get; set; } = true;
    public bool RespectImmutable { get; set; } = true;
    public bool RespectRequestMaxAge { get; set; } = true;
    public bool RespectRequestMaxStale { get; set; } = true;
    public bool RespectRequestMinFresh { get; set; } = true;
    public bool RespectOnlyIfCached { get; set; } = true;
    public bool EnableWarningHeaders { get; set; } = false;
}
