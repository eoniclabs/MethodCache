using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Abstractions.Resolution;
using MethodCache.Core.Configuration.Runtime;
using MethodCache.Abstractions.Sources;
using MethodCache.Core.Configuration.Policies;
using MethodCache.Core.Runtime;

namespace MethodCache.Core.Configuration.Sources;

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
