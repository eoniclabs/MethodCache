using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Abstractions.Resolution;
using MethodCache.Abstractions.Sources;
using MethodCache.Core.Configuration.Policies;

namespace MethodCache.Core.Configuration.Sources;

internal sealed class FluentPolicySource : IPolicySource
{
    private readonly Action<IMethodCacheConfiguration>? _configure;
    private readonly MethodCacheConfiguration? _preconfigured;
    private readonly string _sourceId;

    public FluentPolicySource(Action<IMethodCacheConfiguration> configure)
    {
        _configure = configure ?? throw new ArgumentNullException(nameof(configure));
        _sourceId = PolicySourceIds.StartupFluent;
    }

    public FluentPolicySource(MethodCacheConfiguration configuration)
    {
        _preconfigured = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _sourceId = PolicySourceIds.StartupFluent;
    }

    public string SourceId => _sourceId;

    public Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var configuration = _preconfigured ?? BuildConfigurationFromDelegate();
        var timestamp = DateTimeOffset.UtcNow;
        var snapshots = new List<PolicySnapshot>();

        foreach (var entry in configuration.ToConfigEntries())
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
        => PolicySourceAsyncEnumerable.Empty(cancellationToken);

    private MethodCacheConfiguration BuildConfigurationFromDelegate()
    {
        var configuration = new MethodCacheConfiguration();
        _configure?.Invoke(configuration);
        return configuration;
    }
}
