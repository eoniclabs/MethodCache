using Microsoft.Extensions.Caching.Distributed;

namespace MethodCache.Extensibility;

/// <summary>A distributed cache that can write invalidation timestamps atomically across all its instances.</summary>
/// <remarks>
/// MethodCache requires this capability for its distributed cache. A read followed by an unconditional write
/// cannot provide the contract across processes. Redis and SQL Server providers implement it with backend
/// atomic operations; custom providers must supply the same guarantee.
/// </remarks>
internal interface IAtomicInvalidationStore : IDistributedCache
{
    /// <summary>
    /// Stores the maximum of the incoming timestamp and the unexpired stored timestamp under <paramref name="key"/>.
    /// </summary>
    /// <param name="key">The fully prefixed metadata key.</param>
    /// <param name="timestamp">The invalidation time in Unix milliseconds.</param>
    /// <param name="retention">How long the stamp must remain valid from this write, within the backend's expiration limits.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the atomic write has finished.</returns>
    /// <remarks>
    /// The value returned by <see cref="IDistributedCache.GetAsync"/> must be exactly eight bytes containing the
    /// signed timestamp in little-endian order. The operation must never shorten the expiration of an unexpired
    /// stamp, including when an older write completes later or after its caller has cancelled. Expired stamps
    /// may be replaced. Retention must be positive. Concurrent calls on different cache instances sharing the
    /// same backend must obey these rules.
    /// </remarks>
    Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default);
}
