using MethodCache.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace MethodCache.Core.Runtime.Distributed;

internal sealed partial class DistributedCacheLayer
{
    private string _httpEntryPrefix = null!;
    private string _httpEpochPrefix = null!;
    private Func<int>? _httpPending;
    private Func<ValueTask>? _httpShutdown;
    private Action<string>? _httpTagInvalidated;
    private Action? _httpCleared;
    internal TimeSpan HttpRecoveryInterval => _breakDuration;
    internal string HttpTagAddress(string tag) => _tagPrefix + StoredTag(tag);
    internal string HttpClearAddress => _tagPrefix + ClearTag;
    internal string HttpSlotAddress(int slot) => _httpEpochPrefix + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal bool SupportsHttpEpochs => _epochStore != null;

    internal void ObserveHttpRecovery(Func<int>? pending) => Volatile.Write(ref _httpPending, pending);
    internal void AttachHttp(Action<string> onTag, Action onClear, Func<ValueTask> shutdown)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _httpTagInvalidated = onTag;
            _httpCleared = onClear;
            _httpShutdown = shutdown;
            EnsureSubscription();
        }
    }

    private void EnsureSubscription()
    {
        if (_backplane != null && Interlocked.Exchange(ref _subscribed, 1) == 0)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _subscriptionTask = completion.Task;
            _ = SubscribeAsync(_backplane, completion);
        }
    }

    internal async ValueTask<CacheEpoch[]?> CaptureHttpEpochsAsync(IReadOnlyList<string> keys, CancellationToken token)
    {
        if (_epochStore == null) throw new NotSupportedException("HTTP L2 caching requires the native Redis or SQL Server epoch capability.");
        if (_hasPending || !TryEnter(out var probe) || !await EnsureScopeAsync(probe, token).ConfigureAwait(false)) return null;
        var result = await TryRunAsync(static (layer, addresses, ct) => layer._epochStore!.EnsureEpochsAsync(addresses, ct).AsTask(),
            keys, "read HTTP epochs", probe, token).ConfigureAwait(false);
        return result.Succeeded ? result.Value : null;
    }

    internal async Task<bool> AdvanceHttpSlotAsync(int slot, bool force = false)
    {
        if (_epochStore == null) throw new NotSupportedException("HTTP L2 caching requires the native Redis or SQL Server epoch capability.");
        BeginWrite();
        try
        {
            // Recovery is owned by this layer and is never cancelled by an HTTP caller leaving.
            var probe = NotProbing;
            if ((!force && !TryEnter(out probe)) || !await EnsureScopeAsync(probe, default).ConfigureAwait(false)) return false;
            var result = await TryRunAsync(static (layer, address, ct) => layer._epochStore!.AdvanceEpochAsync(address, ct).AsTask(),
                HttpSlotAddress(slot), "invalidate an HTTP epoch", probe).ConfigureAwait(false);
            return result.Succeeded;
        }
        finally { EndWrite(); }
    }

    internal void ReportLostHttpRecoveries(int count)
        => _logger.LogWarning("MethodCache shutdown lost {Count} HTTP URL invalidations; other instances may read old responses until they expire.", count);

    internal async ValueTask<byte[]?> ReadHttpAsync(string key, byte format, CancellationToken token)
    {
        if (!TryEnter(out var probe)) return null;
        var result = await TryRunAsync(static (layer, physical, ct) => layer._cache.GetAsync(physical, ct),
            _httpEntryPrefix + format.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + key, "read HTTP", probe, token).ConfigureAwait(false);
        return result.Succeeded ? result.Value : null;
    }

    internal async ValueTask<bool> WriteHttpAsync(string key, byte format, byte[] bytes, DateTimeOffset deadline, CancellationToken token)
    {
        if (deadline <= _time.GetUtcNow() || !TryEnter(out var probe)) return false;
        BeginWrite();
        try
        {
            var result = await TryRunAsync(static async (layer, state, ct) =>
            {
                if (state.Deadline <= layer._time.GetUtcNow()) return false;
                await layer._cache.SetAsync(state.Key, state.Bytes,
                    new DistributedCacheEntryOptions { AbsoluteExpiration = state.Deadline }, ct).ConfigureAwait(false);
                return true;
            }, (Key: _httpEntryPrefix + format.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + key, Bytes: bytes, Deadline: deadline), "write", probe, token).ConfigureAwait(false);
            return result.Succeeded && result.Value;
        }
        finally { EndWrite(); }
    }

    internal async ValueTask RemoveHttpAsync(string key, byte format, CancellationToken token)
    {
        if (!TryEnter(out var probe)) return;
        _ = await TryRunAsync(static async (layer, physical, ct) =>
        {
            await layer._cache.RemoveAsync(physical, ct).ConfigureAwait(false);
            return true;
        }, _httpEntryPrefix + format.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + key, "remove HTTP", probe, token).ConfigureAwait(false);
    }
}
