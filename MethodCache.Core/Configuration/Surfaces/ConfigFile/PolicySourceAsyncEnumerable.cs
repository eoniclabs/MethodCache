using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Abstractions.Resolution;

namespace MethodCache.Core.Configuration.Sources;

public static class PolicySourceAsyncEnumerable
{
    public static async IAsyncEnumerable<PolicyChange> Empty([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
