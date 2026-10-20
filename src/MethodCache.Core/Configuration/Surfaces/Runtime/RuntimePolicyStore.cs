using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Infrastructure;
using MethodCache.Policies;

namespace MethodCache.Core.Configuration.Surfaces.Runtime;

internal sealed class RuntimePolicyStore : IDisposable
{
    // Timers can't wait longer than about 49 days; a later expiry is checked again after this
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromDays(1);

    private readonly ConcurrentDictionary<string, StoredPolicy> _policies = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly object _timerSync = new();
    private readonly object _changesSync = new();
    private long _revision;
    private ITimer? _expiryTimer;
    private bool _disposed;

    public RuntimePolicyStore(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    public void Dispose()
    {
        lock (_timerSync)
        {
            _disposed = true;
            _expiryTimer?.Dispose();
            _expiryTimer = null;
        }
    }

    /// <summary>
    /// Raised synchronously, on the thread that made the change, before the call that made it returns. The policy
    /// resolver listens here, so an awaited override is in effect for the next cached call.
    /// </summary>
    internal event Action<PolicyChange>? Changed;

    private sealed record StoredPolicy(
        CachePolicy Policy,
        CachePolicyFields Fields,
        DateTimeOffset Timestamp,
        RuntimeOverrideMetadata? Metadata);

    private const string MetadataOwnerKey = "runtime.owner";
    private const string MetadataReasonKey = "runtime.reason";
    private const string MetadataTicketKey = "runtime.ticket";
    private const string MetadataExpiresAtKey = "runtime.expiresAt";

    internal IReadOnlyList<RuntimeOverride> DescribeOverrides()
    {
        RemoveExpiredPolicies();
        lock (_changesSync)
            return _policies.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(pair => new RuntimeOverride
            {
                MethodId = pair.Key,
                Options = CacheEntryOptions.FromRuntime(CacheRuntimePolicy.FromPolicy(pair.Key, pair.Value.Policy, pair.Value.Fields), preparedKey: false),
                UpdatedAt = pair.Value.Timestamp,
                Metadata = pair.Value.Metadata
            }).ToArray();
    }

    public IReadOnlyCollection<PolicySnapshot> GetSnapshots() => CaptureSnapshots().Snapshots;

    internal (IReadOnlyCollection<PolicySnapshot> Snapshots, long Revision) CaptureSnapshots()
    {
        RemoveExpiredPolicies();
        lock (_changesSync)
        {
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
            return (snapshots, _revision);
        }
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

        var timestamp = _time.GetUtcNow();
        var policyWithMetadata = ApplyRuntimeMetadata(policy, metadata);
        if (policyWithMetadata.Metadata.Count > 0) fields |= CachePolicyFields.Metadata;
        var contributionMetadata = BuildContributionMetadata(metadata);
        var enriched = CachePolicyMapper.AttachContribution(policyWithMetadata, PolicySourceIds.RuntimeOverrides, fields, timestamp, contributionMetadata);
        var stored = new StoredPolicy(enriched, fields, timestamp, metadata);

        PolicyChange change;
        lock (_changesSync)
        {
            var reason = _policies.ContainsKey(methodId) ? PolicyChangeReason.Updated : PolicyChangeReason.Added;
            _policies[methodId] = stored;
            change = PolicySnapshotBuilder.CreateChange(
                PolicySourceIds.RuntimeOverrides, methodId, enriched, fields, reason, timestamp, contributionMetadata);
            change.SourceRevision = ++_revision;
        }
        // Never hold the mutation lock while invoking observers: snapshot initialization takes the resolver lock
        // first, and observers can also make reentrant overrides. Revisions preserve order across either case.
        EnqueueChange(change);

        if (metadata?.ExpiresAt != null)
        {
            ScheduleExpiry();
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string methodId)
    {
        if (string.IsNullOrWhiteSpace(methodId))
        {
            throw new ArgumentException("Method id must be provided.", nameof(methodId));
        }

        PolicyChange? change = null;
        lock (_changesSync)
        {
            if (_policies.TryRemove(methodId, out _))
            {
                change = CreateRemovedChange(methodId, "Runtime override removed");
            }
        }
        if (change != null)
        {
            EnqueueChange(change);
        }
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        var changes = new List<PolicyChange>();
        lock (_changesSync)
        {
            foreach (var key in _policies.Keys)
            {
                if (_policies.TryRemove(key, out _))
                {
                    changes.Add(CreateRemovedChange(key, "Runtime overrides cleared"));
                }
            }
        }
        foreach (var change in changes)
        {
            EnqueueChange(change);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Streams the changes made after the call. Each watcher gets every change.
    /// </summary>
    public async IAsyncEnumerable<PolicyChange> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<PolicyChange>(new UnboundedChannelOptions { SingleReader = true });
        void Handler(PolicyChange change) => channel.Writer.TryWrite(change);

        Changed += Handler;
        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var change))
                {
                    yield return change;
                }
            }
        }
        finally
        {
            Changed -= Handler;
        }
    }

    private void EnqueueChange(PolicyChange change)
    {
        Changed?.Invoke(change);
    }

    /// <summary>
    /// Sets the timer for the next override to expire, so it stops applying on time rather than at the next change.
    /// </summary>
    private void ScheduleExpiry()
    {
        lock (_timerSync)
        {
            if (_disposed)
            {
                return;
            }

            DateTimeOffset? next = null;
            foreach (var stored in _policies.Values)
            {
                if (stored.Metadata?.ExpiresAt is { } expiresAt && (next == null || expiresAt < next))
                {
                    next = expiresAt;
                }
            }

            _expiryTimer?.Dispose();
            _expiryTimer = null;
            if (next == null)
            {
                return;
            }

            var delay = next.Value - _time.GetUtcNow();
            delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay > MaxTimerDelay ? MaxTimerDelay : delay;
            _expiryTimer = _time.CreateTimer(static state =>
            {
                var store = (RuntimePolicyStore)state!;
                store.RemoveExpiredPolicies();
                store.ScheduleExpiry();
            }, this, delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void RemoveExpiredPolicies()
    {
        var now = _time.GetUtcNow();
        List<PolicyChange>? changes = null;
        lock (_changesSync)
        {
            foreach (var (methodId, stored) in _policies)
            {
                if (stored.Metadata?.ExpiresAt is not { } expiresAt || expiresAt > now)
                {
                    continue;
                }
                if (_policies.TryRemove(new KeyValuePair<string, StoredPolicy>(methodId, stored)))
                {
                    (changes ??= new List<PolicyChange>()).Add(CreateRemovedChange(methodId, "Runtime override expired", BuildContributionMetadata(stored.Metadata)));
                }
            }
        }
        if (changes != null)
        {
            foreach (var change in changes)
            {
                EnqueueChange(change);
            }
        }
    }

    // Called while holding _changesSync, so the revision describes the actual mutation order.
    private PolicyChange CreateRemovedChange(string methodId, string notes, IReadOnlyDictionary<string, string?>? metadata = null)
    {
        var timestamp = _time.GetUtcNow();
        var policy = CachePolicyMapper.AttachContribution(
            CachePolicy.Empty,
            PolicySourceIds.RuntimeOverrides,
            CachePolicyFields.None,
            timestamp,
            metadata: metadata,
            notes: notes);
        var change = PolicySnapshotBuilder.CreateChange(
            PolicySourceIds.RuntimeOverrides,
            methodId,
            policy,
            CachePolicyFields.None,
            PolicyChangeReason.Removed,
            timestamp,
            metadata);
        change.SourceRevision = ++_revision;
        return change;
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
