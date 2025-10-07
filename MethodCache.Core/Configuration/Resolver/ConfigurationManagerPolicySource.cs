using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Resolution;
using MethodCache.Abstractions.Sources;
using MethodCache.Core.Configuration.Policies;

namespace MethodCache.Core.Configuration.Resolver;

internal sealed class ConfigurationManagerPolicySource : IPolicySource
{
    private readonly IMethodCacheConfigurationManager _manager;
    private readonly string _sourceId;
    private readonly object _sync = new();
    private IReadOnlyDictionary<string, CacheMethodSettings> _snapshot = new Dictionary<string, CacheMethodSettings>(StringComparer.Ordinal);
    private Channel<PolicyChange>? _channel;

    public ConfigurationManagerPolicySource(IMethodCacheConfigurationManager manager, string sourceId = "configuration")
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _sourceId = sourceId;
        _manager.ConfigurationChanged += OnConfigurationChanged;
    }

    public string SourceId => _sourceId;

    public Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configurations = _manager.GetAllConfigurations();
        lock (_sync)
        {
            _snapshot = configurations;
        }

        var timestamp = DateTimeOffset.UtcNow;
        var snapshots = new List<PolicySnapshot>(configurations.Count);
        foreach (var kvp in configurations)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key))
            {
                continue;
            }

            snapshots.Add(BuildSnapshot(kvp.Key, kvp.Value, timestamp));
        }

        return Task.FromResult<IReadOnlyCollection<PolicySnapshot>>(snapshots);
    }

    public IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<PolicyChange>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false
        });

        lock (_sync)
        {
            _channel = channel;
        }

        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    private void OnConfigurationChanged(object? sender, ConfigurationChangedEventArgs e)
    {
        Channel<PolicyChange>? channel;
        lock (_sync)
        {
            channel = _channel;
        }

        if (channel == null)
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow;
        var current = _manager.GetAllConfigurations();

        IReadOnlyDictionary<string, CacheMethodSettings> previous;
        lock (_sync)
        {
            previous = _snapshot;
            _snapshot = current;
        }

        foreach (var kvp in current)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key))
            {
                continue;
            }

            var policySnapshot = BuildSnapshot(kvp.Key, kvp.Value, timestamp);
            var fields = CachePolicyMapper.DetectFields(policySnapshot.Policy);
            var delta = new CachePolicyDelta(fields, CachePolicyFields.None, policySnapshot.Policy);
            channel.Writer.TryWrite(new PolicyChange(_sourceId, kvp.Key, delta, PolicyChangeReason.Reloaded, timestamp));
        }

        foreach (var kvp in previous)
        {
            if (!current.ContainsKey(kvp.Key))
            {
                var delta = new CachePolicyDelta(CachePolicyFields.None, CachePolicyFields.Duration | CachePolicyFields.Tags | CachePolicyFields.KeyGenerator | CachePolicyFields.Version | CachePolicyFields.Metadata | CachePolicyFields.RequireIdempotent, CachePolicy.Empty);
                channel.Writer.TryWrite(new PolicyChange(_sourceId, kvp.Key, delta, PolicyChangeReason.Removed, timestamp));
            }
        }
    }

    private PolicySnapshot BuildSnapshot(string methodKey, CacheMethodSettings settings, DateTimeOffset timestamp)
    {
        var (policy, fields) = CachePolicyMapper.FromSettings(settings);
        policy = CachePolicyMapper.AttachContribution(policy, _sourceId, fields, timestamp);
        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["source"] = _sourceId
        };
        return new PolicySnapshot(_sourceId, methodKey, policy, timestamp, metadata);
    }
}
