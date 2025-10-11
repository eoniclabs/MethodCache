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
    private readonly IReadOnlyList<PolicyDraft> _policies;
    private readonly string _sourceId;

    public ConfigFilePolicySource(IEnumerable<PolicyDraft> policies)
    {
        if (policies == null)
        {
            throw new ArgumentNullException(nameof(policies));
        }

        _policies = policies
            .Select(static draft =>
            {
                var fields = draft.Fields == CachePolicyFields.None
                    ? CachePolicyMapper.DetectFields(draft.Policy)
                    : draft.Fields;

                return new PolicyDraft(draft.MethodId, draft.Policy, fields, draft.Metadata, draft.Notes);
            })
            .ToArray();

        _sourceId = PolicySourceIds.ConfigurationFiles;
    }

    public string SourceId => _sourceId;

    public async Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var timestamp = DateTimeOffset.UtcNow;
        var snapshots = new List<PolicySnapshot>(_policies.Count);

        foreach (var draft in _policies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshots.Add(PolicySnapshotBuilder.FromPolicy(
                _sourceId,
                draft.MethodId,
                draft.Policy,
                draft.Fields,
                timestamp,
                draft.Metadata,
                draft.Notes));
        }

        return await Task.FromResult<IReadOnlyCollection<PolicySnapshot>>(snapshots).ConfigureAwait(false);
    }

    public IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default)
        => PolicySourceAsyncEnumerable.Empty(cancellationToken);
}
