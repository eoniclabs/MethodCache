using MethodCache.Core.Configuration.Surfaces.Runtime;
using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Policies;

namespace MethodCache.Core.PolicyPipeline.Sources;

internal sealed class RuntimePolicySource : IImmediatePolicySource
{
    private readonly RuntimePolicyStore _store;

    public RuntimePolicySource(RuntimePolicyStore store)
    {
        _store = store;
    }

    public string SourceId => PolicySourceIds.RuntimeOverrides;

    public Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_store.GetSnapshots());

    public IReadOnlyCollection<PolicySnapshot> GetSnapshot() => _store.GetSnapshots();

    public event Action<PolicyChange>? Changed
    {
        add => _store.Changed += value;
        remove => _store.Changed -= value;
    }

    public IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default)
        => _store.WatchAsync(cancellationToken);
}
