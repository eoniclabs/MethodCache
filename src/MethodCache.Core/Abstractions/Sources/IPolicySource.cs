using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;

namespace MethodCache.Policies;

/// <summary>
/// A place cache policies come from, such as attributes, startup code, configuration files or runtime overrides.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPolicySource
{
    /// <summary>
    /// A stable identifier for the source, such as <c>attributes</c> or <c>configuration-files</c>.
    /// </summary>
    string SourceId { get; }

    /// <summary>
    /// Returns every policy the source currently defines.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One snapshot per method.</returns>
    Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams changes to the source's policies while the app runs; a source that never changes can complete immediately.
    /// </summary>
    /// <param name="cancellationToken">Stops the stream.</param>
    /// <returns>The changes, as they happen.</returns>
    IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default);
}
