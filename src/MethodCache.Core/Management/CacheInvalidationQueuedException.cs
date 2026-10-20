namespace MethodCache;

/// <summary>
/// Thrown by <see cref="ICacheAdministrator"/> when an invalidation was applied in memory but the distributed cache did
/// not confirm it yet: it is queued and retried in the background. <c>RemoveEntryAsync</c>, <c>InvalidateTagsAsync</c>,
/// <c>InvalidateMethodAsync</c> and <c>ClearAsync</c> throw it; HttpClient caching reports the same condition as
/// <c>HttpCacheInvalidationException</c>. Do not retry the operation that caused it; the cache owns recovery, and
/// <see cref="ICacheAdministrator.GetInvalidationQueues"/> shows what is still pending.
/// </summary>
public sealed class CacheInvalidationQueuedException : InvalidOperationException
{
    /// <summary>Creates the exception with a default message.</summary>
    public CacheInvalidationQueuedException()
        : base("Cache invalidation is queued for recovery and has not been confirmed by distributed storage.")
    {
    }

    /// <summary>Creates the exception with a message describing what is queued.</summary>
    public CacheInvalidationQueuedException(string? message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message describing what is queued and the exception that caused it.</summary>
    public CacheInvalidationQueuedException(string? message, Exception? innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// How many targets each stage reached: <see cref="InvalidationResult.Queued"/> counts the ones waiting for the
    /// distributed cache. <c>null</c> when the operation reports no counts, as <c>ClearAsync</c> does.
    /// </summary>
    public InvalidationResult? Result { get; init; }

    /// <summary>Throws when <paramref name="result"/> has targets still waiting for the distributed cache.</summary>
    internal static void ThrowIfQueued(InvalidationResult result)
    {
        if (result.Queued > 0)
        {
            throw new CacheInvalidationQueuedException(
                "Cache invalidation is queued for recovery and has not been confirmed by distributed storage.")
            {
                Result = result
            };
        }
    }
}
