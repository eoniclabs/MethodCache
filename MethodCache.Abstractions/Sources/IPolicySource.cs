using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using MethodCache.Abstractions.Resolution;

namespace MethodCache.Abstractions.Sources;

public interface IPolicySource
{
    string SourceId { get; }

    Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default);
}
