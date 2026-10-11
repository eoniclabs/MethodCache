using System.ComponentModel;
namespace MethodCache.Policies;

/// <summary>
/// A policy source registered with the resolver, and its priority.
/// </summary>
/// <param name="Source">The policy source.</param>
/// <param name="Priority">The source's priority; higher wins per field. The built-in sources use 10 (attributes), 40 (startup code), 50 (configuration files) and 100 (runtime overrides).</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record PolicySourceRegistration(IPolicySource Source, int Priority)
{
    /// <summary>
    /// The identifier of <see cref="Source"/>.
    /// </summary>
    public string SourceId => Source.SourceId;
}
