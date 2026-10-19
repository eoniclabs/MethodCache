namespace MethodCache;

/// <summary>
/// Thrown when an invalidation was applied in memory but the distributed cache did not confirm it yet: it is queued
/// and retried in the background. <see cref="ICacheAdministrator"/> throws it from <c>RemoveEntryAsync</c>,
/// <c>InvalidateTagsAsync</c> and <c>ClearAsync</c>, and HTTP caching throws it after a mutating request whose cache
/// invalidation could not be confirmed. Do not retry the operation that caused it; the cache owns recovery.
/// </summary>
public sealed class CacheInvalidationQueuedException : InvalidOperationException
{
    /// <summary>Creates the exception with a message describing what is queued.</summary>
    public CacheInvalidationQueuedException(string message) : base(message) { }
}
