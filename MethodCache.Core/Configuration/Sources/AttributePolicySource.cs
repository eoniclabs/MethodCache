using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Abstractions.Resolution;
using MethodCache.Abstractions.Sources;
using MethodCache.Core.Configuration.Policies;

namespace MethodCache.Core.Configuration.Sources;

internal sealed class AttributePolicySource : IPolicySource
{
    private readonly AttributeConfigurationSource _legacySource;
    private readonly string _sourceId;

    public AttributePolicySource(params Assembly[] assemblies)
    {
        _legacySource = new AttributeConfigurationSource(assemblies);
        _sourceId = PolicySourceIds.Attributes;
    }

    public string SourceId => _sourceId;

    public async Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = await _legacySource.LoadAsync().ConfigureAwait(false);
        var timestamp = DateTimeOffset.UtcNow;

        return entries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.MethodKey))
            .Select(entry => PolicySnapshotBuilder.FromConfigEntry(_sourceId, entry, timestamp))
            .ToArray();
    }

    public IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default)
        => PolicySourceAsyncEnumerable.Empty(cancellationToken);
}
