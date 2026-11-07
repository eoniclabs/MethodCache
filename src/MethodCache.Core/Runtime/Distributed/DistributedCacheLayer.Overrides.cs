using Microsoft.Extensions.Caching.Distributed;

namespace MethodCache.Core.Runtime.Distributed;

// The runtime overrides every instance applies: one record per method, read when an instance starts and again when the
// backplane says the method changed. Not the entry format, so they can outlive TagRetention.
internal sealed partial class DistributedCacheLayer
{
    // Records without an end are kept this long, which every provider stores as an absolute expiration
    private static readonly TimeSpan OverrideRetention = TimeSpan.FromDays(3650);

    /// <summary>Whether a backplane tells the other instances about invalidations, and so about changed overrides.</summary>
    internal bool HasBackplane => _backplane != null;

    /// <summary>How long one call to the distributed cache may take.</summary>
    internal TimeSpan OperationTimeout => _timeout;

    /// <summary>How long the cache waits before it tries the distributed cache again after a failure.</summary>
    internal TimeSpan RetryInterval => _breakDuration;

    private string OverrideKey(string methodId) => _keyPrefix + "override:v1:" + methodId;

    /// <summary>The stored override record of <paramref name="methodId"/>, or null. Throws when the cache doesn't answer.</summary>
    internal async Task<byte[]?> ReadOverrideAsync(string methodId, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_timeout, _time);
        return await _cache.GetAsync(OverrideKey(methodId), timeout.Token).WaitAsync(_timeout, _time, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores the override record of <paramref name="methodId"/> until <paramref name="expiresAt"/>, or for good.</summary>
    internal async Task WriteOverrideAsync(string methodId, byte[] record, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_timeout, _time);
        var options = new DistributedCacheEntryOptions { AbsoluteExpiration = expiresAt ?? _time.GetUtcNow() + OverrideRetention };
        await _cache.SetAsync(OverrideKey(methodId), record, options, timeout.Token).WaitAsync(_timeout, _time, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells the other instances that the method's override changed, so they read it again, for a change that
    /// invalidated nothing here. A message the backplane doesn't take is published again later.
    /// </summary>
    internal async Task PublishOverrideChangedAsync(string methodId)
    {
        var target = (true, MethodTag(methodId));
        if (!await PublishTargetAsync(target, report: false).ConfigureAwait(false)) QueuePublication(target, report: false);
    }

    /// <summary>Removes the override record of <paramref name="methodId"/>.</summary>
    internal async Task RemoveOverrideAsync(string methodId, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_timeout, _time);
        await _cache.RemoveAsync(OverrideKey(methodId), timeout.Token).WaitAsync(_timeout, _time, cancellationToken).ConfigureAwait(false);
    }
}
