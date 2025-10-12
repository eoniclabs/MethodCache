using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Resolution;
using MethodCache.Core.PolicyPipeline.Model;

namespace MethodCache.Core.Configuration.Surfaces.Runtime;

internal sealed class RuntimePolicyStore
{
    private readonly ConcurrentDictionary<string, StoredPolicy> _policies = new(StringComparer.Ordinal);
    private readonly Channel<PolicyChange> _channel = Channel.CreateUnbounded<PolicyChange>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });

    private sealed record StoredPolicy(CachePolicy Policy, CachePolicyFields Fields, DateTimeOffset Timestamp);

    public IReadOnlyCollection<PolicySnapshot> GetSnapshots()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = new List<PolicySnapshot>(_policies.Count);

        foreach (var (methodId, stored) in _policies)
        {
            snapshots.Add(PolicySnapshotBuilder.FromPolicy(
                PolicySourceIds.RuntimeOverrides,
                methodId,
                stored.Policy,
                stored.Fields,
                stored.Timestamp,
                metadata: null));
        }

        return snapshots;
    }

    public Task UpsertAsync(string methodId, CachePolicy policy, CachePolicyFields fields)
    {
        if (string.IsNullOrWhiteSpace(methodId))
        {
            throw new ArgumentException("Method id must be provided.", nameof(methodId));
        }

        if (policy == null)
        {
            throw new ArgumentNullException(nameof(policy));
        }

        if (fields == CachePolicyFields.None)
        {
            fields = CachePolicyMapper.DetectFields(policy);
        }

        var timestamp = DateTimeOffset.UtcNow;
        var enriched = CachePolicyMapper.AttachContribution(policy, PolicySourceIds.RuntimeOverrides, fields, timestamp);
        var stored = new StoredPolicy(enriched, fields, timestamp);

        var reason = PolicyChangeReason.Added;
        _policies.AddOrUpdate(
            methodId,
            _ => stored,
            (_, _) =>
            {
                reason = PolicyChangeReason.Updated;
                return stored;
            });

        EnqueueChange(PolicySnapshotBuilder.CreateChange(
            PolicySourceIds.RuntimeOverrides,
            methodId,
            enriched,
            fields,
            reason,
            timestamp));

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string methodId)
    {
        if (string.IsNullOrWhiteSpace(methodId))
        {
            throw new ArgumentException("Method id must be provided.", nameof(methodId));
        }

        if (_policies.TryRemove(methodId, out _))
        {
            var timestamp = DateTimeOffset.UtcNow;
            var policy = CachePolicyMapper.AttachContribution(
                CachePolicy.Empty,
                PolicySourceIds.RuntimeOverrides,
                CachePolicyFields.None,
                timestamp,
                notes: "Runtime override removed");

            EnqueueChange(PolicySnapshotBuilder.CreateChange(
                PolicySourceIds.RuntimeOverrides,
                methodId,
                policy,
                CachePolicyFields.None,
                PolicyChangeReason.Removed,
                timestamp));
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        if (_policies.IsEmpty)
        {
            return Task.CompletedTask;
        }

        foreach (var key in _policies.Keys)
        {
            _policies.TryRemove(key, out _);
            var timestamp = DateTimeOffset.UtcNow;
            var policy = CachePolicyMapper.AttachContribution(
                CachePolicy.Empty,
                PolicySourceIds.RuntimeOverrides,
                CachePolicyFields.None,
                timestamp,
                notes: "Runtime overrides cleared");

            EnqueueChange(PolicySnapshotBuilder.CreateChange(
                PolicySourceIds.RuntimeOverrides,
                key,
                policy,
                CachePolicyFields.None,
                PolicyChangeReason.Removed,
                timestamp));
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<PolicyChange> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reader = _channel.Reader;

        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var change))
            {
                yield return change;
            }
        }
    }

    private void EnqueueChange(PolicyChange change)
    {
        _channel.Writer.TryWrite(change);
    }
}
