namespace MethodCache.Core.PolicyPipeline.Resolution;

/// <summary>
/// Tells the policy registry, on this thread, how the other instances learn of a change to one method's runtime
/// override that this instance applies. The runtime store raises the change on the calling thread before it returns,
/// so the registry sees the scope while it handles that change.
/// </summary>
internal sealed class OverrideChangeScope : IDisposable
{
    [ThreadStatic] private static OverrideChangeScope? t_current;

    private readonly OverrideChangeScope? _outer;

    private OverrideChangeScope(string methodId, bool invalidatedElsewhere)
    {
        MethodId = methodId;
        InvalidatedElsewhere = invalidatedElsewhere;
        _outer = t_current;
        t_current = this;
    }

    /// <summary>The method whose override changes.</summary>
    public string MethodId { get; }

    /// <summary>
    /// The instance that made the change has already invalidated the method on every instance, so this one only
    /// drops its own entries.
    /// </summary>
    public bool InvalidatedElsewhere { get; }

    /// <summary>The registry invalidated the method on every instance, which tells the others of the change.</summary>
    public bool Spread { get; private set; }

    public static OverrideChangeScope Begin(string methodId, bool invalidatedElsewhere) => new(methodId, invalidatedElsewhere);

    /// <summary>The scope of a change to <paramref name="methodId"/> on this thread, or null.</summary>
    internal static OverrideChangeScope? For(string methodId)
        => t_current is { } scope && string.Equals(scope.MethodId, methodId, StringComparison.Ordinal) ? scope : null;

    internal void MarkSpread() => Spread = true;

    public void Dispose() => t_current = _outer;
}
