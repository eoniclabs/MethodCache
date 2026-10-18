namespace MethodCache.HttpCaching;

/// <summary>The HTTP origin mutation may have succeeded, but its cache invalidation was not confirmed.
/// Recovery belongs to the cache; retrying the origin mutation automatically can repeat its side effects.</summary>
public sealed class HttpCacheInvalidationException : Exception
{
    /// <summary>Creates an unconfirmed invalidation error without recording request or response contents.</summary>
    public HttpCacheInvalidationException() : base("The HTTP origin mutation may have succeeded, but cache invalidation was not confirmed. Cache recovery continues independently; do not retry the origin mutation automatically.") { }
}
