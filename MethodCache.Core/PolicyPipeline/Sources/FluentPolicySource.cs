using MethodCache.Abstractions.Resolution;
using MethodCache.Abstractions.Sources;
using MethodCache.Core.Configuration.Surfaces.ConfigFile;
using MethodCache.Core.Configuration.Surfaces.Fluent;
using MethodCache.Core.PolicyPipeline.Model;

namespace MethodCache.Core.PolicyPipeline.Sources;

internal sealed class FluentPolicySource : IPolicySource
{
    private readonly IReadOnlyList<PolicyDraft> _compiledPolicies;
    private readonly string _sourceId;

    public FluentPolicySource(Action<IFluentMethodCacheConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _sourceId = PolicySourceIds.StartupFluent;
        _compiledPolicies = CompilePolicies(configure);
    }

    public string SourceId => _sourceId;

    public Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var timestamp = DateTimeOffset.UtcNow;
        var snapshots = new List<PolicySnapshot>(_compiledPolicies.Count);

        foreach (var draft in _compiledPolicies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshots.Add(PolicySnapshotBuilder.FromPolicy(_sourceId, draft.MethodId, draft.Policy, draft.Fields, timestamp, draft.Metadata, draft.Notes));
        }

        return Task.FromResult<IReadOnlyCollection<PolicySnapshot>>(snapshots);
    }

    public IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default)
        => PolicySourceAsyncEnumerable.Empty(cancellationToken);

    private static IReadOnlyList<PolicyDraft> CompilePolicies(Action<IFluentMethodCacheConfiguration> configure)
    {
        var fluent = new FluentMethodCacheConfiguration();
        configure(fluent);

        return fluent.BuildMethodPolicies();
    }
}
