using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Resolution;
using MethodCache.Abstractions.Sources;
using MethodCache.Core.Configuration.Policies;
using MethodCache.Core.Configuration.Sources;

namespace MethodCache.Core.Configuration.Sources;

internal sealed class ConfigFilePolicySource : IPolicySource
{
    private readonly IReadOnlyList<(string MethodId, CachePolicy Policy, CachePolicyFields Fields, IReadOnlyDictionary<string, string?>? Metadata, string? Notes)> _policies;
    private readonly string _sourceId;

    public ConfigFilePolicySource(IEnumerable<ConfigFilePolicySourceBuilder.PolicyDescriptor> policies)
    {
        if (policies == null)
        {
            throw new ArgumentNullException(nameof(policies));
        }

        _policies = policies.Select(static descriptor =>
        {
            var fields = descriptor.Fields == CachePolicyFields.None
                ? CachePolicyMapper.DetectFields(descriptor.Policy)
                : descriptor.Fields;

            return (descriptor.MethodId, descriptor.Policy, fields, descriptor.Metadata, descriptor.Notes);
        }).ToArray();

        _sourceId = PolicySourceIds.ConfigurationFiles;
    }

    public string SourceId => _sourceId;

    public async Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var timestamp = DateTimeOffset.UtcNow;
        var snapshots = _policies.Select(policy =>
        {
            var enriched = CachePolicyMapper.AttachContribution(
                policy.Policy,
                _sourceId,
                policy.Fields,
                timestamp,
                policy.Metadata,
                policy.Notes);

            return PolicySnapshotBuilder.FromPolicy(
                _sourceId,
                policy.MethodId,
                enriched,
                policy.Fields,
                timestamp,
                policy.Metadata,
                policy.Notes);
        }).ToArray();

        return await Task.FromResult<IReadOnlyCollection<PolicySnapshot>>(snapshots).ConfigureAwait(false);
    }

    public IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default)
        => PolicySourceAsyncEnumerable.Empty(cancellationToken);
}
