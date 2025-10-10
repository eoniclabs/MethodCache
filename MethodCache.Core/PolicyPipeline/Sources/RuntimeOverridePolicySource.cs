using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Resolution;
using MethodCache.Abstractions.Sources;
using MethodCache.Core.Configuration.Policies;

namespace MethodCache.Core.Configuration.Runtime;

internal sealed class RuntimeOverridePolicySource : IPolicySource
{
    private readonly RuntimePolicyOverrideStore _store;
    private readonly string _sourceId = PolicySourceIds.RuntimeOverrides;

    public RuntimeOverridePolicySource(RuntimePolicyOverrideStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string SourceId => _sourceId;

    public Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = _store.GetOverrides();
        var timestamp = DateTimeOffset.UtcNow;
        var snapshots = new List<PolicySnapshot>(entries.Count);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.MethodKey))
            {
                continue;
            }

            snapshots.Add(PolicySnapshotBuilder.FromConfigEntry(_sourceId, entry, timestamp));
        }

        return Task.FromResult<IReadOnlyCollection<PolicySnapshot>>(snapshots);
    }

    public IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default)
        => WatchInternal(cancellationToken);

    private async IAsyncEnumerable<PolicyChange> WatchInternal([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<RuntimeOverridesChangedEventArgs>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        void Handler(object? sender, RuntimeOverridesChangedEventArgs args)
        {
            channel.Writer.TryWrite(args);
        }

        _store.OverridesChanged += Handler;

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var args))
                {
                    foreach (var change in CreateChanges(args))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return change;
                    }
                }
            }
        }
        finally
        {
            _store.OverridesChanged -= Handler;
            channel.Writer.TryComplete();
        }
    }

    private IEnumerable<PolicyChange> CreateChanges(RuntimeOverridesChangedEventArgs args)
    {
        var timestamp = DateTimeOffset.UtcNow;

        switch (args.Kind)
        {
            case RuntimeOverrideChangeKind.Upsert:
                foreach (var methodKey in args.AffectedMethodKeys)
                {
                    if (!_store.TryGetOverride(methodKey, out var settings))
                    {
                        continue;
                    }

                    var (policy, fields) = CachePolicyMapper.FromSettings(settings);
                    policy = CachePolicyMapper.AttachContribution(policy, _sourceId, fields, timestamp);
                    yield return PolicySnapshotBuilder.CreateChange(_sourceId, methodKey, policy, fields, PolicyChangeReason.Updated, timestamp);
                }
                break;

            case RuntimeOverrideChangeKind.Removed:
            case RuntimeOverrideChangeKind.Cleared:
                foreach (var methodKey in args.AffectedMethodKeys)
                {
                    var emptyPolicy = CachePolicyMapper.AttachContribution(CachePolicy.Empty, _sourceId, CachePolicyFields.None, timestamp, notes: "Override removed");
                    yield return PolicySnapshotBuilder.CreateChange(_sourceId, methodKey, emptyPolicy, CachePolicyFields.None, PolicyChangeReason.Removed, timestamp);
                }
                break;
        }
    }
}

