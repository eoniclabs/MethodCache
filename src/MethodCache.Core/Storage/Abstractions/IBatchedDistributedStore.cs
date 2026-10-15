using MethodCache.Infrastructure;

namespace MethodCache.Extensibility;

internal readonly record struct DistributedRead(byte[]? Value, byte[]? RemovalStamp);

internal interface IBatchedDistributedStore
{
    ValueTask<DistributedRead> ReadEntryAsync(string valueKey, string removalKey, CancellationToken cancellationToken = default);
}

internal interface IDistributedEpochStore
{
    ValueTask<CacheEpoch[]> EnsureEpochsAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken = default);
    ValueTask<CacheEpoch> AdvanceEpochAsync(string key, CancellationToken cancellationToken = default);
    ValueTask<TimeSpan> EnsureRetentionContractAsync(string key, TimeSpan requested, CancellationToken cancellationToken = default);
}
