namespace MethodCache;

/// <summary>
/// Metadata associated with a runtime override.
/// </summary>
public sealed record RuntimeOverrideMetadata(
    string? Owner = null,
    string? Reason = null,
    string? Ticket = null,
    DateTimeOffset? ExpiresAt = null);
