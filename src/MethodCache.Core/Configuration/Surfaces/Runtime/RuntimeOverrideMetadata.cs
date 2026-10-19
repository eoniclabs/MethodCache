namespace MethodCache;

/// <summary>
/// Metadata associated with a runtime override.
/// </summary>
public sealed record RuntimeOverrideMetadata
{
    /// <summary>Who made the override.</summary>
    public string? Owner { get; init; }
    /// <summary>Why the override was made.</summary>
    public string? Reason { get; init; }
    /// <summary>A ticket or incident reference.</summary>
    public string? Ticket { get; init; }
    /// <summary>When the override is removed automatically, or <c>null</c> to keep it.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}
