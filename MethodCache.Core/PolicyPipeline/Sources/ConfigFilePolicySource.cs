using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Abstractions.Resolution;
using MethodCache.Abstractions.Sources;
using MethodCache.Core.Configuration.Policies;

namespace MethodCache.Core.Configuration.Sources;

internal sealed class ConfigFilePolicySource : IPolicySource
{
    private readonly IReadOnlyList<IConfigurationSource> _sources;
    private readonly string _sourceId;

    public ConfigFilePolicySource(IEnumerable<IConfigurationSource> sources)
    {
        if (sources == null)
        {
            throw new ArgumentNullException(nameof(sources));
        }

        _sources = sources.ToArray();
        if (_sources.Count == 0)
        {
            throw new ArgumentException("At least one configuration source must be provided.", nameof(sources));
        }

        _sourceId = PolicySourceIds.ConfigurationFiles;
    }

    public string SourceId => _sourceId;

    public async Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var timestamp = DateTimeOffset.UtcNow;
        var snapshots = new List<PolicySnapshot>();

        foreach (var source in _sources)
        {
            var entries = await source.LoadAsync().ConfigureAwait(false);

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.MethodKey))
                {
                    continue;
                }

                snapshots.Add(PolicySnapshotBuilder.FromConfigEntry(_sourceId, entry, timestamp));
            }
        }

        return snapshots;
    }

    public IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default)
        => PolicySourceAsyncEnumerable.Empty(cancellationToken);
}
