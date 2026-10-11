using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.Policies;

public interface IPolicyResolver
{
    ValueTask<PolicyResolutionResult> ResolveAsync(string methodId, CancellationToken cancellationToken = default);

    IAsyncEnumerable<PolicyResolutionResult> WatchAsync(string? methodId = null, CancellationToken cancellationToken = default);
}
