namespace MethodCache;

/// <summary>
/// Describes a runtime override: who made it and why, for the audit trail, and when it ends. At
/// <see cref="ExpiresAt"/> the override is removed automatically and the method goes back to its other settings.
/// </summary>
public sealed record RuntimeOverrideMetadata
{
    /// <summary>Who made the override.</summary>
    public string? Owner { get; init; }
    /// <summary>Why the override was made.</summary>
    public string? Reason { get; init; }
    /// <summary>A ticket or incident reference.</summary>
    public string? Ticket { get; init; }
    /// <summary>
    /// When the override is removed automatically, as <see cref="IRuntimeCacheConfigurator.RemoveAsync"/> would remove
    /// it; <c>null</c> keeps it until it is removed.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}
