using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.Policies;

/// <summary>
/// Merges the policies from all registered sources into one effective policy per method; higher-priority sources win per field.
/// </summary>
internal interface IPolicyResolver
{
    /// <summary>
    /// Returns the effective policy for a method, loading the sources first if needed; a method without any policy gets an empty one.
    /// </summary>
    /// <param name="methodId">The method identifier.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The resolved policy and the contributions that built it.</returns>
    ValueTask<PolicyResolutionResult> ResolveAsync(string methodId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams a new result each time an effective policy changes.
    /// </summary>
    /// <param name="methodId">Only report changes for this method, or all methods when null.</param>
    /// <param name="cancellationToken">Stops the stream.</param>
    /// <returns>The updated policies, as they change.</returns>
    IAsyncEnumerable<PolicyResolutionResult> WatchAsync(string? methodId = null, CancellationToken cancellationToken = default);
}
