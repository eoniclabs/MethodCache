using System.Runtime.CompilerServices;
using MethodCache.Abstractions.Resolution;

namespace MethodCache.Core.Configuration.Surfaces.ConfigFile;

public static class PolicySourceAsyncEnumerable
{
    public static async IAsyncEnumerable<PolicyChange> Empty([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
