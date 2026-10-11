using System.ComponentModel;
using System.Runtime.CompilerServices;
using MethodCache.Policies;

namespace MethodCache.Infrastructure;

[EditorBrowsable(EditorBrowsableState.Never)]
public static class PolicySourceAsyncEnumerable
{
    public static async IAsyncEnumerable<PolicyChange> Empty([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
