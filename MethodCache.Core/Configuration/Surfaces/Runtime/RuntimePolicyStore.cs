using System.Collections.Concurrent;
using System.Globalization;
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

    private sealed record StoredPolicy(
        CachePolicy Policy,
        CachePolicyFields Fields,
        DateTimeOffset Timestamp,
        RuntimeOverrideMetadata? Metadata);

    private const string MetadataOwnerKey = "runtime.owner";
    private const string MetadataReasonKey = "runtime.reason";
    private const string MetadataTicketKey = "runtime.ticket";
    private const string MetadataExpiresAtKey = "runtime.expiresAt";

    public IReadOnlyCollection<PolicySnapshot> GetSnapshots()
    {
        RemoveExpiredPolicies();
        var snapshots = new List<PolicySnapshot>(_policies.Count);

        foreach (var (methodId, stored) in _policies)
        {
            snapshots.Add(PolicySnapshotBuilder.FromPolicy(
                PolicySourceIds.RuntimeOverrides,
                methodId,
                stored.Policy,
                stored.Fields,
                stored.Timestamp,
                metadata: stored.Policy.Metadata));
        }

        return snapshots;
    }

    public PolicySnapshot? GetSnapshot(string methodId)
    {
        if (string.IsNullOrWhiteSpace(methodId))
        {
            throw new ArgumentException("Method id must be provided.", nameof(methodId));
        }

        RemoveExpiredPolicies();

        if (!_policies.TryGetValue(methodId, out var stored))
        {
            return null;
        }

        return PolicySnapshotBuilder.FromPolicy(
            PolicySourceIds.RuntimeOverrides,
            methodId,
            stored.Policy,
            stored.Fields,
            stored.Timestamp,
            metadata: stored.Policy.Metadata);
    }

    public Task UpsertAsync(string methodId, CachePolicy policy, CachePolicyFields fields, RuntimeOverrideMetadata? metadata = null)
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

        RemoveExpiredPolicies();

        var timestamp = DateTimeOffset.UtcNow;
        var policyWithMetadata = ApplyRuntimeMetadata(policy, metadata);
        var contributionMetadata = BuildContributionMetadata(metadata);
        var enriched = CachePolicyMapper.AttachContribution(policyWithMetadata, PolicySourceIds.RuntimeOverrides, fields, timestamp, contributionMetadata);
        var stored = new StoredPolicy(enriched, fields, timestamp, metadata);

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
                timestamp,
                contributionMetadata));

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
            EnqueueRemovedChange(methodId, "Runtime override removed");
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
            if (_policies.TryRemove(key, out _))
            {
                EnqueueRemovedChange(key, "Runtime overrides cleared");
            }
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

    private void RemoveExpiredPolicies()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var (methodId, stored) in _policies)
        {
            var expiresAt = stored.Metadata?.ExpiresAt;
            if (!expiresAt.HasValue || expiresAt.Value > now)
            {
                continue;
            }

            if (_policies.TryRemove(methodId, out var removed))
            {
                var metadata = BuildContributionMetadata(removed.Metadata);
                EnqueueRemovedChange(methodId, "Runtime override expired", metadata);
            }
        }
    }

    private void EnqueueRemovedChange(string methodId, string notes, IReadOnlyDictionary<string, string?>? metadata = null)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var policy = CachePolicyMapper.AttachContribution(
            CachePolicy.Empty,
            PolicySourceIds.RuntimeOverrides,
            CachePolicyFields.None,
            timestamp,
            metadata: metadata,
            notes: notes);

        EnqueueChange(PolicySnapshotBuilder.CreateChange(
            PolicySourceIds.RuntimeOverrides,
            methodId,
            policy,
            CachePolicyFields.None,
            PolicyChangeReason.Removed,
            timestamp,
            metadata));
    }

    private static CachePolicy ApplyRuntimeMetadata(CachePolicy policy, RuntimeOverrideMetadata? metadata)
    {
        if (metadata == null)
        {
            return policy;
        }

        var merged = new Dictionary<string, string?>(policy.Metadata, StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(metadata.Owner))
        {
            merged[MetadataOwnerKey] = metadata.Owner;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Reason))
        {
            merged[MetadataReasonKey] = metadata.Reason;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Ticket))
        {
            merged[MetadataTicketKey] = metadata.Ticket;
        }

        if (metadata.ExpiresAt.HasValue)
        {
            merged[MetadataExpiresAtKey] = metadata.ExpiresAt.Value.ToString("O", CultureInfo.InvariantCulture);
        }

        return policy with { Metadata = merged };
    }

    private static IReadOnlyDictionary<string, string?>? BuildContributionMetadata(RuntimeOverrideMetadata? metadata)
    {
        if (metadata == null)
        {
            return null;
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(metadata.Owner))
        {
            values[MetadataOwnerKey] = metadata.Owner;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Reason))
        {
            values[MetadataReasonKey] = metadata.Reason;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Ticket))
        {
            values[MetadataTicketKey] = metadata.Ticket;
        }

        if (metadata.ExpiresAt.HasValue)
        {
            values[MetadataExpiresAtKey] = metadata.ExpiresAt.Value.ToString("O", CultureInfo.InvariantCulture);
        }

        return values.Count == 0 ? null : values;
    }
}
