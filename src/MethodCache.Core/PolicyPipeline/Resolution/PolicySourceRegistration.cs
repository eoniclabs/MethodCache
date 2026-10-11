namespace MethodCache.Policies;

/// <summary>
/// A policy source registered with the resolver, and its priority.
/// </summary>
/// <param name="Source">The policy source.</param>
/// <param name="Priority">The source's priority; higher wins per field. See <see cref="PolicySourcePriority"/>.</param>
public sealed record PolicySourceRegistration(IPolicySource Source, int Priority)
{
    /// <summary>
    /// The identifier of <see cref="Source"/>.
    /// </summary>
    public string SourceId => Source.SourceId;
}
