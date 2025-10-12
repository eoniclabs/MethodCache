using MethodCache.Abstractions.Resolution;
using MethodCache.Abstractions.Sources;
using MethodCache.Core.Configuration.Surfaces.Runtime;
using MethodCache.Core.PolicyPipeline.Model;

namespace MethodCache.Core.PolicyPipeline.Sources;

internal sealed class RuntimePolicySource : IPolicySource
{
    private readonly RuntimePolicyStore _store;

    public RuntimePolicySource(RuntimePolicyStore store)
    {
        _store = store;
    }

    public string SourceId => PolicySourceIds.RuntimeOverrides;

    public Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_store.GetSnapshots());

    public IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default)
        => _store.WatchAsync(cancellationToken);
}
