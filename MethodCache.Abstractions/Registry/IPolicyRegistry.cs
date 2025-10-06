using System.Collections.Generic;
using System.Threading;

using MethodCache.Abstractions.Resolution;

namespace MethodCache.Abstractions.Registry;

public interface IPolicyRegistry
{
    PolicyResolutionResult GetPolicy(string methodId);

    IReadOnlyCollection<PolicyResolutionResult> GetAllPolicies();

    IAsyncEnumerable<PolicyResolutionResult> WatchAsync(CancellationToken cancellationToken = default);
}
