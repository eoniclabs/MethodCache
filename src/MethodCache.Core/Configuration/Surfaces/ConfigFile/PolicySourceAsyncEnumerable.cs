using System.ComponentModel;
using System.Runtime.CompilerServices;
using MethodCache.Policies;

namespace MethodCache.Infrastructure;

/// <summary>
/// Helpers for policy sources that never change while the application runs. Public only for generated code.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class PolicySourceAsyncEnumerable
{
    /// <summary>
    /// A change stream that completes without producing any changes.
    /// </summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>An empty sequence of <see cref="PolicyChange"/>.</returns>
    public static async IAsyncEnumerable<PolicyChange> Empty([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
